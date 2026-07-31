namespace SsdpCore
{
    /// <summary>
    /// WS-Discovery protocol constants.
    /// Uses the same multicast group as SSDP (239.255.255.250) but port 3702.
    /// Windows Explorer's Network folder uses WS-Discovery to find devices.
    /// </summary>
    public static class WsdConstants
    {
        /// <summary>
        /// WS-Discovery port (3702)
        /// </summary>
        public const int Port = 3702;

        /// <summary>
        /// Same multicast group as SSDP
        /// </summary>
        public const string MulticastAddress = "239.255.255.250";

        /// <summary>
        /// Multicast TTL for WSD packets
        /// </summary>
        public const int MulticastTtl = 2;

        // XML namespaces (April 2005 versions — what Windows uses)
        public const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
        public const string NsWsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        public const string NsWsd = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
        public const string NsWsdp = "http://schemas.xmlsoap.org/ws/2006/02/devprof";
        public const string NsWsx = "http://schemas.xmlsoap.org/ws/2004/09/mex";

        // WS-Addressing actions
        public const string ActionHello = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Hello";
        public const string ActionBye = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Bye";
        public const string ActionProbe = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe";
        public const string ActionProbeMatches = "http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches";
        public const string ActionGetResponse = "http://schemas.xmlsoap.org/ws/2004/09/transfer/GetResponse";
        public const string ActionGet = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Get";

        // WS-Discovery device type for Device Profile for Web Services
        public const string DeviceType = "wsdp:Device";

        // Metadata version for Hello/Bye (incremented on metadata changes)
        public const int MetadataVersion = 1;

        /// <summary>
        /// Content type for SOAP over HTTP
        /// </summary>
        public const string SoapContentType = "application/soap+xml";
    }
}
