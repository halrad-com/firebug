namespace SsdpCore
{
    /// <summary>
    /// SSDP protocol constants
    /// </summary>
    public static class SsdpConstants
    {
        /// <summary>
        /// SSDP multicast address (239.255.255.250)
        /// </summary>
        public const string MulticastAddress = "239.255.255.250";

        /// <summary>
        /// SSDP port (1900)
        /// </summary>
        public const int Port = 1900;

        /// <summary>
        /// Default cache control max-age in seconds (30 minutes)
        /// </summary>
        public const int DefaultMaxAge = 1800;

        /// <summary>
        /// Multicast TTL for SSDP packets
        /// </summary>
        public const int MulticastTtl = 2;

        // Common search targets
        public const string SearchTargetAll = "ssdp:all";
        public const string SearchTargetRootDevice = "upnp:rootdevice";
        public const string SearchTargetAvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
        public const string SearchTargetMediaRenderer = "urn:schemas-upnp-org:device:MediaRenderer:1";

        // Header names (case-insensitive in SSDP)
        public const string HeaderHost = "HOST";
        public const string HeaderCacheControl = "CACHE-CONTROL";
        public const string HeaderLocation = "LOCATION";
        public const string HeaderST = "ST";              // Search Target (in responses)
        public const string HeaderNT = "NT";              // Notification Type (in NOTIFY)
        public const string HeaderNTS = "NTS";            // Notification Sub-Type
        public const string HeaderUSN = "USN";            // Unique Service Name
        public const string HeaderServer = "SERVER";
        public const string HeaderMAN = "MAN";            // Mandatory extension
        public const string HeaderMX = "MX";              // Maximum wait time
        public const string HeaderDate = "DATE";
        public const string HeaderExt = "EXT";

        // NTS values
        public const string NtsAlive = "ssdp:alive";
        public const string NtsByeBye = "ssdp:byebye";
    }
}
