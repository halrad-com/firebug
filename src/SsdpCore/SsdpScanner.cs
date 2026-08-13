using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SsdpCore
{
    /// <summary>
    /// One-shot SSDP discovery sweep — the M-SEARCH counterpart of
    /// <see cref="MdnsScanner"/>: construct with an optional log callback,
    /// call <c>ScanAsync</c>, get a deduplicated list.
    ///
    /// Built on <see cref="SsdpClient"/> (which owns the socket, multicast join
    /// and response parsing): starts listening, sends M-SEARCH for each search
    /// target twice (UDP — a single datagram is a coin toss), collects matching
    /// responses and NOTIFY announcements for the scan duration, then stops.
    ///
    /// By default results come back NAMED: after the listen window closes, the
    /// UPnP description XML behind each device's LOCATION is fetched in parallel
    /// (via <see cref="SsdpDescription"/>) to fill FriendlyName / Manufacturer /
    /// ModelName. Pass <c>fetchDescriptions: false</c> for a raw wire-level sweep.
    /// Note the <see cref="DeviceFound"/> event fires at DISCOVERY time, before
    /// any description has been fetched.
    /// </summary>
    public class SsdpScanner : IDisposable
    {
        // Enrichment bounds — SSDP responses are unauthenticated datagrams, so
        // result counts are attacker-inflatable and each enrichment is a GET.
        private const int MaxEnrichedDevices = 64;
        private const int EnrichConcurrency = 8;

        private readonly Action<TraceLevel, string> _log;
        private readonly IPAddress _localAddress;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly object _scanLock = new object();
        private volatile bool _scanning;
        private volatile bool _disposed;

        /// <summary>Raised once per unique device as it is discovered during a scan.</summary>
        public event EventHandler<SsdpDevice> DeviceFound;

        /// <param name="log">Optional logging callback.</param>
        /// <param name="localAddress">NIC to scan from. Null picks the first
        /// non-virtual IPv4 interface (<see cref="SsdpClient.GetPreferredLocalAddress"/>);
        /// multi-homed hosts that need a specific segment pass it explicitly.</param>
        public SsdpScanner(Action<TraceLevel, string> log = null, IPAddress localAddress = null)
        {
            _log = SsdpTrace.Wrap(log);
            _localAddress = localAddress;
        }

        /// <summary>
        /// Convenience overload for a single search target
        /// (e.g. <see cref="SsdpConstants.SearchTargetMediaRenderer"/>).
        /// One scan runs at a time per instance — a concurrent call logs a
        /// warning and returns an empty list.
        /// </summary>
        public Task<List<SsdpDevice>> ScanAsync(
            string searchTarget,
            int scanDurationMs = 3000,
            bool fetchDescriptions = true,
            CancellationToken cancellationToken = default)
        {
            return ScanAsync(new[] { searchTarget }, scanDurationMs, fetchDescriptions, cancellationToken);
        }

        /// <summary>
        /// Scan for SSDP devices matching the given search targets. Sends an
        /// M-SEARCH per target and listens for the scan duration. Results are
        /// deduplicated by device address + type and, unless
        /// <paramref name="fetchDescriptions"/> is false, enriched with the
        /// name fields from each device's description XML before returning.
        /// One scan runs at a time per instance — a concurrent call logs a
        /// warning and returns an empty list.
        /// </summary>
        public async Task<List<SsdpDevice>> ScanAsync(
            IEnumerable<string> searchTargets,
            int scanDurationMs = 3000,
            bool fetchDescriptions = true,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SsdpScanner));

            lock (_scanLock)
            {
                if (_scanning)
                {
                    _log(TraceLevel.Warning, "SSDP scan already in progress");
                    return new List<SsdpDevice>();
                }
                _scanning = true;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token))
            {
                try
                {
                    return await ScanInternalAsync(searchTargets.ToList(), scanDurationMs, fetchDescriptions, linked.Token);
                }
                finally
                {
                    lock (_scanLock) { _scanning = false; }
                }
            }
        }

        private async Task<List<SsdpDevice>> ScanInternalAsync(
            List<string> targets, int scanDurationMs, bool fetchDescriptions, CancellationToken ct)
        {
            var display = string.Join(", ", targets);
            _log(TraceLevel.Info, $"Starting SSDP scan for [{display}] ({scanDurationMs}ms)");

            var results = new Dictionary<string, SsdpDevice>(StringComparer.OrdinalIgnoreCase);

            using (var client = new SsdpClient(_log, _localAddress ?? SsdpClient.GetPreferredLocalAddress()))
            {
                EventHandler<SsdpDevice> onFound = (sender, device) =>
                {
                    // The shared socket also hears unsolicited NOTIFY chatter from the
                    // whole LAN; only devices matching a requested target count.
                    if (!MatchesAny(device, targets)) return;

                    var key = (device.Address?.ToString() ?? "?") + "|" + (device.DeviceType ?? "?");
                    lock (results)
                    {
                        if (results.ContainsKey(key)) return;
                        results[key] = device;
                    }
                    _log(TraceLevel.Info, $"Found: {device.DeviceType} at {device.Address} ({device.Location})");
                    DeviceFound?.Invoke(this, device);
                };

                client.DeviceFound += onFound;
                try
                {
                    client.StartListening();

                    // MX asks responders to spread replies over that many seconds —
                    // keep it inside the window we actually listen for.
                    var mx = Math.Max(1, Math.Min(5, scanDurationMs / 1000));
                    foreach (var t in targets) client.SendSearch(t, mx);

                    // Re-send once for reliability (same pattern as MdnsScanner).
                    var resendDelay = Math.Min(250, scanDurationMs);
                    await Task.Delay(resendDelay, ct);
                    foreach (var t in targets) client.SendSearch(t, mx);

                    var remaining = scanDurationMs - resendDelay;
                    if (remaining > 0) await Task.Delay(remaining, ct);
                }
                catch (OperationCanceledException)
                {
                    _log(TraceLevel.Info, _disposed ? "SSDP scan stopped (disposing)" : "SSDP scan cancelled");
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"SSDP scan failed: {ex.Message}");
                }
                finally
                {
                    client.DeviceFound -= onFound;
                }
            }

            List<SsdpDevice> list;
            lock (results) { list = results.Values.ToList(); }

            if (fetchDescriptions && list.Count > 0 && !ct.IsCancellationRequested)
            {
                // Enrichment is bounded on BOTH axes because every "device" here
                // is just an unauthenticated UDP datagram — one hostile node can
                // spoof any number of them, and each enrichment is an HTTP GET.
                // Cap the count (a real LAN has dozens of renderers, not
                // hundreds) and the concurrency, and say so when truncating.
                var toEnrich = list;
                if (list.Count > MaxEnrichedDevices)
                {
                    _log(TraceLevel.Warning,
                        $"Scan produced {list.Count} devices; enriching only the first {MaxEnrichedDevices} (flood?)");
                    toEnrich = list.Take(MaxEnrichedDevices).ToList();
                }
                try
                {
                    using (var gate = new SemaphoreSlim(EnrichConcurrency, EnrichConcurrency))
                    {
                        var fetches = toEnrich.Select(async d =>
                        {
                            await gate.WaitAsync(ct).ConfigureAwait(false);
                            try { await SsdpDescription.FetchAsync(d, cancellationToken: ct, log: _log).ConfigureAwait(false); }
                            finally { gate.Release(); }
                        }).ToArray();
                        await Task.WhenAll(fetches);
                    }
                }
                catch (OperationCanceledException) { /* cancelled mid-enrichment — partial names are fine */ }
                var named = list.Count(d => d.DescriptionFetched);
                _log(TraceLevel.Info, $"Fetched descriptions for {named}/{list.Count} device(s)");
            }

            _log(TraceLevel.Info, $"SSDP scan complete: {list.Count} device(s) found");
            return list;
        }

        private static bool MatchesAny(SsdpDevice device, List<string> targets)
        {
            foreach (var t in targets)
            {
                if (string.IsNullOrEmpty(t)) continue;
                // "ssdp:all" means every response is a match by definition.
                if (string.Equals(t, SsdpConstants.SearchTargetAll, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (device.DeviceType != null &&
                    device.DeviceType.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Cancel, never Dispose: an in-flight ScanAsync holds a token source
            // LINKED to this one (consumers genuinely dispose the scanner while a
            // scan is running — a sibling task throwing out of a using block is
            // enough), and disposing a CTS others are linked to is a race that
            // surfaces as ObjectDisposedException from CreateLinkedTokenSource.
            // A process-lifetime CTS with no timer leaks nothing worth the race.
            try { _disposeCts.Cancel(); } catch { }
            DeviceFound = null;
        }
    }
}
