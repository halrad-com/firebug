using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SsdpCore
{
    /// <summary>
    /// Represents a discovered SSDP/UPnP device
    /// </summary>
    public class SsdpDevice
    {
        /// <summary>
        /// Unique device identifier (USN or UDN)
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Device type or service type from ST/NT header
        /// </summary>
        public string DeviceType { get; set; }

        /// <summary>
        /// URL to the device description XML (from LOCATION header)
        /// </summary>
        public string Location { get; set; }

        /// <summary>
        /// IP address of the device
        /// </summary>
        public IPAddress Address { get; set; }

        /// <summary>
        /// When the device was last seen via SSDP
        /// </summary>
        public DateTime LastSeen { get; set; }

        /// <summary>
        /// Cache-control max-age value in seconds (how long until device expires)
        /// </summary>
        public int MaxAge { get; set; } = SsdpConstants.DefaultMaxAge;

        // Fields populated after fetching device description XML

        /// <summary>
        /// Human-readable device name from device description
        /// </summary>
        public string FriendlyName { get; set; }

        /// <summary>
        /// Device manufacturer from device description
        /// </summary>
        public string Manufacturer { get; set; }

        /// <summary>
        /// Model name from device description
        /// </summary>
        public string ModelName { get; set; }

        /// <summary>
        /// Model description from device description
        /// </summary>
        public string ModelDescription { get; set; }

        /// <summary>
        /// Presentation URL (web interface) from device description
        /// </summary>
        public string PresentationUrl { get; set; }

        /// <summary>
        /// SERVER header value from SSDP response
        /// </summary>
        public string ServerString { get; set; }

        /// <summary>
        /// Whether the device description has been fetched and parsed
        /// </summary>
        public bool DescriptionFetched { get; set; }

        /// <summary>
        /// Returns whether this device entry has expired based on MaxAge
        /// </summary>
        public bool IsExpired => DateTime.UtcNow > LastSeen.AddSeconds(MaxAge);

        /// <summary>
        /// Fetch this device's UPnP description XML (from <see cref="Location"/>)
        /// and populate the description fields above. Convenience wrapper over
        /// <see cref="SsdpDescription.FetchAsync"/> — never throws, returns false
        /// if the device would not describe itself (or the fetch was refused as
        /// off-LAN; pass <paramref name="log"/> to see which).
        /// </summary>
        public Task<bool> FetchDescriptionAsync(
            int timeoutMs = SsdpDescription.DefaultTimeoutMs,
            CancellationToken cancellationToken = default,
            Action<System.Diagnostics.TraceLevel, string> log = null)
        {
            return SsdpDescription.FetchAsync(this, timeoutMs, cancellationToken, log);
        }

        public override string ToString()
        {
            return FriendlyName ?? ModelName ?? Id ?? Location;
        }

        /// <summary>
        /// Create a device from an SSDP response message
        /// </summary>
        public static SsdpDevice FromSsdpResponse(string message, IPAddress sourceAddress)
        {
            var location = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderLocation);
            if (string.IsNullOrEmpty(location)) return null;

            var st = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderST);
            var nt = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderNT);
            var usn = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderUSN);
            var server = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderServer);
            var cacheControl = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderCacheControl);

            var maxAge = SsdpConstants.DefaultMaxAge;
            if (!string.IsNullOrEmpty(cacheControl))
            {
                // Parse "max-age = 1800" or "max-age=1800"
                var parts = cacheControl.Split('=');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var parsed))
                {
                    maxAge = parsed;
                }
            }

            return new SsdpDevice
            {
                Id = usn ?? location,
                DeviceType = st ?? nt,
                Location = location,
                Address = sourceAddress,
                LastSeen = DateTime.UtcNow,
                MaxAge = maxAge,
                ServerString = server
            };
        }
    }
}
