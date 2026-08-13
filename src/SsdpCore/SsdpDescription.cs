using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SsdpCore
{
    /// <summary>
    /// Fetches and parses UPnP device descriptions — the XML document behind a
    /// discovered device's LOCATION URL — and fills the description fields on
    /// <see cref="SsdpDevice"/> (<c>FriendlyName</c>, <c>Manufacturer</c>,
    /// <c>ModelName</c>, <c>ModelDescription</c>, <c>PresentationUrl</c>).
    ///
    /// Device-agnostic by design: only the standard UPnP Device Architecture
    /// elements of the ROOT device are read, matched by local name so vendor
    /// namespace variations don't matter. Embedded (nested) devices are ignored.
    ///
    /// Never throws: a device that won't describe itself — timeout, HTTP error,
    /// malformed XML — simply stays unnamed, and the method returns false.
    ///
    /// LAN-scoped on purpose: LOCATION is attacker-controllable (any device on
    /// the multicast group chooses it), so fetches are refused unless the URL's
    /// host is an IP literal in private, link-local or ULA space. A discovery
    /// library has no business following a LAN advertisement out to the
    /// internet — or to a DNS name that can re-resolve anywhere.
    /// </summary>
    public static class SsdpDescription
    {
        /// <summary>Default per-fetch deadline (ms) — the single source for it.</summary>
        public const int DefaultTimeoutMs = 1500;

        // Description documents are a few KB; a malicious LOCATION should not
        // be able to make us buffer an arbitrary body. Oversized responses fail
        // into the ordinary stays-unnamed path.
        private const int MaxDescriptionBytes = 256 * 1024;

        // Redirects are legitimate (a device http->https'ing itself, a port
        // move) but must NEVER leave the LAN. Auto-follow stays OFF because it
        // is the UNVALIDATED path (.NET would follow up to 50 hops without
        // re-checking the target); FetchAsync follows redirects itself,
        // pushing EVERY hop through the same LAN-scope gate as the original
        // LOCATION. A device that needs more hops than this is broken.
        private const int MaxRedirectHops = 3;

        // One shared client for all fetches (socket pooling); per-call deadlines
        // come from a linked CancellationTokenSource, not HttpClient.Timeout.
        private static readonly HttpClient _http = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            MaxResponseContentBufferSize = MaxDescriptionBytes
        };

        /// <summary>
        /// Fetch and parse the description XML at <paramref name="device"/>.Location.
        /// On success the device's description fields are populated and
        /// <see cref="SsdpDevice.DescriptionFetched"/> is set.
        /// </summary>
        /// <param name="device">A discovered device with a LOCATION URL.</param>
        /// <param name="timeoutMs">Per-fetch deadline. Keep it short — description
        /// fetches usually run in bulk after a scan, and one dead device must not
        /// stall the batch.</param>
        /// <param name="cancellationToken">Optional external cancellation.</param>
        /// <param name="log">Optional logging callback (failures log at Verbose).</param>
        /// <returns>true if the description was fetched and parsed.</returns>
        public static async Task<bool> FetchAsync(
            SsdpDevice device,
            int timeoutMs = DefaultTimeoutMs,
            CancellationToken cancellationToken = default,
            Action<TraceLevel, string> log = null)
        {
            log = SsdpTrace.Wrap(log);
            if (device == null || string.IsNullOrEmpty(device.Location))
                return false;

            if (!IsLanScopedLocation(device.Location, out var locationUri))
            {
                log(TraceLevel.Verbose, $"Refusing description fetch — LOCATION is not a LAN-scoped IP URL: {device.Location}");
                return false;
            }

            try
            {
                string xml;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // ONE deadline for the whole redirect chain — the clock is
                    // never reset per hop, so a batch of fetches still costs
                    // one timeout, redirects or not.
                    cts.CancelAfter(timeoutMs);
                    var current = locationUri;
                    HttpResponseMessage response = null;
                    try
                    {
                        for (var hop = 0; ; hop++)
                        {
                            // Default completion option buffers the whole body, so
                            // the deadline covers the read as well as the connect.
                            response = await _http.GetAsync(current, cts.Token).ConfigureAwait(false);
                            var status = (int)response.StatusCode;
                            var isRedirect = status == 301 || status == 302 || status == 303
                                          || status == 307 || status == 308;
                            if (!isRedirect) break;

                            if (hop >= MaxRedirectHops)
                            {
                                log(TraceLevel.Verbose, $"Refusing description fetch — redirect chain exceeds {MaxRedirectHops} hops at {current}");
                                return false;
                            }
                            var location = response.Headers.Location;
                            if (location == null)
                            {
                                log(TraceLevel.Verbose, $"Refusing description fetch — redirect without Location from {current}");
                                return false;
                            }
                            // Resolve against the CURRENT uri so relative redirects
                            // work — a relative Location keeps the already-validated
                            // IP-literal host and passes the gate naturally.
                            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                            if (!IsLanScopedLocation(next.AbsoluteUri, out var validated))
                            {
                                log(TraceLevel.Verbose, $"Refusing off-LAN redirect from {current} to {next}");
                                return false;
                            }
                            response.Dispose();
                            response = null;
                            current = validated;
                        }

                        response.EnsureSuccessStatusCode();
                        // Bytes, not ReadAsStringAsync: embedded devices routinely
                        // send Content-Type charsets .NET rejects as invalid (WiiM
                        // and GUPnP both send charset="utf-8" WITH quotes, which
                        // throws on .NET Framework). The header is untrustworthy;
                        // decode as BOM-sniffed UTF-8, which is what UPnP
                        // descriptions are in practice.
                        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        using (var reader = new System.IO.StreamReader(
                            new System.IO.MemoryStream(bytes), System.Text.Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true))
                        {
                            xml = reader.ReadToEnd();
                        }
                    }
                    finally
                    {
                        response?.Dispose();
                    }
                }

                var doc = XDocument.Parse(xml);
                // First <device> in document order is the root device; embedded
                // devices sit deeper inside its <deviceList> and come later.
                var root = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                if (root == null)
                {
                    log(TraceLevel.Verbose, $"No <device> element in description at {device.Location}");
                    return false;
                }

                // Direct children only — a root device must not inherit an
                // embedded sub-device's name.
                string Child(string name) =>
                    root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

                device.FriendlyName = Child("friendlyName");
                device.Manufacturer = Child("manufacturer");
                device.ModelName = Child("modelName");
                device.ModelDescription = Child("modelDescription");
                device.PresentationUrl = Child("presentationURL");
                device.DescriptionFetched = true;
                return true;
            }
            catch (Exception ex)
            {
                log(TraceLevel.Verbose, $"Description fetch failed for {device.Location}: {ex.Message}");
                return false;
            }
        }

        // LOCATION must be an http(s) URL whose host is an IP LITERAL in
        // private (RFC 1918), link-local (169.254/16, fe80::/10) or unique-local
        // (fc00::/7) space. DNS names are refused outright — a hostname can
        // resolve anywhere, including back out to the internet, and a discovery
        // library has no business following a LAN advertisement off the LAN.
        private static bool IsLanScopedLocation(string location, out Uri uri)
        {
            uri = null;
            if (!Uri.TryCreate(location, UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (!IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var ip)) return false;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                var ok = b[0] == 10
                      || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                      || (b[0] == 192 && b[1] == 168)
                      || (b[0] == 169 && b[1] == 254);
                if (!ok) return false;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                var uniqueLocal = (b[0] & 0xFE) == 0xFC;
                if (!ip.IsIPv6LinkLocal && !uniqueLocal) return false;
            }
            else
            {
                return false;
            }

            uri = parsed;
            return true;
        }
    }
}
