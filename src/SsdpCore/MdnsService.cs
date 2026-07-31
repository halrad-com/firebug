using System;
using System.Collections.Generic;
using System.Net;

namespace SsdpCore
{
    /// <summary>
    /// Represents a service discovered via mDNS/DNS-SD.
    /// </summary>
    public class MdnsService
    {
        /// <summary>
        /// Friendly name of the service instance (e.g. "Living Room Speaker").
        /// Extracted from the mDNS instance name before the service type suffix.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// IP address of the host providing the service.
        /// </summary>
        public string IPAddress { get; set; }

        /// <summary>
        /// Port the service is listening on.
        /// </summary>
        public ushort Port { get; set; }

        /// <summary>
        /// Hostname from the SRV record (e.g. "Phantom-Premier.local.").
        /// </summary>
        public string HostName { get; set; }

        /// <summary>
        /// The service type that was queried (e.g. "_devialet._tcp.local.").
        /// </summary>
        public string ServiceType { get; set; }

        /// <summary>
        /// TXT record key-value pairs, if any.
        /// </summary>
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

        public override string ToString()
        {
            return $"{Name} ({IPAddress}:{Port})";
        }
    }
}
