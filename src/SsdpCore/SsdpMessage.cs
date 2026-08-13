using System;
using System.Collections.Generic;
using System.Text;

namespace SsdpCore
{
    /// <summary>
    /// SSDP message parsing and building utilities
    /// </summary>
    public static class SsdpMessage
    {
        private const string Crlf = "\r\n";

        #region Parsing

        /// <summary>
        /// Parse a single header value from an SSDP message
        /// </summary>
        public static string ParseHeader(string message, string headerName)
        {
            if (string.IsNullOrEmpty(message)) return null;

            var lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(headerName.Length + 1).Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// Parse all headers from an SSDP message into a dictionary
        /// </summary>
        public static Dictionary<string, string> ParseHeaders(string message)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(message)) return headers;

            var lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    headers[key] = value;
                }
            }
            return headers;
        }

        /// <summary>
        /// Check if message is an M-SEARCH request
        /// </summary>
        public static bool IsMSearch(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                   message.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if message is a NOTIFY message
        /// </summary>
        public static bool IsNotify(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                   message.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if message is an HTTP 200 OK response (search response)
        /// </summary>
        public static bool IsSearchResponse(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                   message.StartsWith("HTTP/1.1 200 OK", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Building - Client (M-SEARCH)

        /// <summary>
        /// Build an M-SEARCH discovery request
        /// </summary>
        /// <param name="searchTarget">The search target (ST header), e.g. "ssdp:all" or "urn:schemas-upnp-org:service:AVTransport:1"</param>
        /// <param name="mx">Maximum wait time in seconds (1-5 recommended)</param>
        public static string BuildMSearch(string searchTarget, int mx = 3)
        {
            // ST is interpolated into a CRLF-framed message — a search target
            // containing CR/LF would inject headers. Reject it here so no
            // consumer has to remember to.
            if (searchTarget != null && (searchTarget.IndexOf('\r') >= 0 || searchTarget.IndexOf('\n') >= 0))
                throw new ArgumentException("Search target must not contain CR or LF characters.", nameof(searchTarget));

            var sb = new StringBuilder();
            sb.Append("M-SEARCH * HTTP/1.1").Append(Crlf);
            sb.Append($"HOST: {SsdpConstants.MulticastAddress}:{SsdpConstants.Port}").Append(Crlf);
            sb.Append("MAN: \"ssdp:discover\"").Append(Crlf);
            sb.Append($"MX: {mx}").Append(Crlf);
            sb.Append($"ST: {searchTarget}").Append(Crlf);
            sb.Append(Crlf);
            return sb.ToString();
        }

        #endregion

        #region Building - Server (NOTIFY, Search Response)

        /// <summary>
        /// Build a NOTIFY ssdp:alive message for advertising a device
        /// </summary>
        /// <param name="location">URL to the device description XML</param>
        /// <param name="nt">Notification Type (device type or service type)</param>
        /// <param name="usn">Unique Service Name (usually uuid:xxx::nt)</param>
        /// <param name="serverString">SERVER header value</param>
        /// <param name="maxAge">Cache control max-age in seconds</param>
        public static string BuildNotifyAlive(string location, string nt, string usn, string serverString, int maxAge = SsdpConstants.DefaultMaxAge)
        {
            var sb = new StringBuilder();
            sb.Append("NOTIFY * HTTP/1.1").Append(Crlf);
            sb.Append($"HOST: {SsdpConstants.MulticastAddress}:{SsdpConstants.Port}").Append(Crlf);
            sb.Append($"CACHE-CONTROL: max-age = {maxAge}").Append(Crlf);
            sb.Append($"LOCATION: {location}").Append(Crlf);
            sb.Append($"NT: {nt}").Append(Crlf);
            sb.Append($"NTS: {SsdpConstants.NtsAlive}").Append(Crlf);
            sb.Append($"SERVER: {serverString}").Append(Crlf);
            sb.Append($"USN: {usn}").Append(Crlf);
            sb.Append(Crlf);
            return sb.ToString();
        }

        /// <summary>
        /// Build a NOTIFY ssdp:byebye message for removing a device
        /// </summary>
        /// <param name="nt">Notification Type</param>
        /// <param name="usn">Unique Service Name</param>
        public static string BuildNotifyByeBye(string nt, string usn)
        {
            var sb = new StringBuilder();
            sb.Append("NOTIFY * HTTP/1.1").Append(Crlf);
            sb.Append($"HOST: {SsdpConstants.MulticastAddress}:{SsdpConstants.Port}").Append(Crlf);
            sb.Append($"NT: {nt}").Append(Crlf);
            sb.Append($"NTS: {SsdpConstants.NtsByeBye}").Append(Crlf);
            sb.Append($"USN: {usn}").Append(Crlf);
            sb.Append(Crlf);
            return sb.ToString();
        }

        /// <summary>
        /// Build an M-SEARCH response message
        /// </summary>
        /// <param name="location">URL to the device description XML</param>
        /// <param name="st">Search Target being responded to</param>
        /// <param name="usn">Unique Service Name</param>
        /// <param name="serverString">SERVER header value</param>
        /// <param name="maxAge">Cache control max-age in seconds</param>
        public static string BuildSearchResponse(string location, string st, string usn, string serverString, int maxAge = SsdpConstants.DefaultMaxAge)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK").Append(Crlf);
            sb.Append($"CACHE-CONTROL: max-age = {maxAge}").Append(Crlf);
            sb.Append($"DATE: {DateTime.UtcNow:r}").Append(Crlf);
            sb.Append("EXT:").Append(Crlf);
            sb.Append($"LOCATION: {location}").Append(Crlf);
            sb.Append($"SERVER: {serverString}").Append(Crlf);
            sb.Append($"ST: {st}").Append(Crlf);
            sb.Append($"USN: {usn}").Append(Crlf);
            sb.Append(Crlf);
            return sb.ToString();
        }

        /// <summary>
        /// Build a standard SERVER header value
        /// </summary>
        /// <param name="productName">Product name (e.g. "MBXHub")</param>
        /// <param name="productVersion">Product version</param>
        public static string BuildServerString(string productName, string productVersion)
        {
            return $"Windows/{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} UPnP/1.1 {productName}/{productVersion}";
        }

        /// <summary>
        /// Build a USN from UUID and notification type
        /// </summary>
        /// <param name="uuid">Device UUID (without "uuid:" prefix)</param>
        /// <param name="nt">Notification type (null for root device UUID only)</param>
        public static string BuildUsn(string uuid, string nt)
        {
            if (string.IsNullOrEmpty(nt) || nt == $"uuid:{uuid}")
            {
                return $"uuid:{uuid}";
            }
            return $"uuid:{uuid}::{nt}";
        }

        #endregion
    }
}
