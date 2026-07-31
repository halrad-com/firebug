using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SsdpCore
{
    /// <summary>
    /// Raw mDNS/DNS-SD scanner using UDP multicast — no Zeroconf dependency.
    /// Same pattern as <see cref="SsdpClient"/>: opens a UDP socket, sends
    /// DNS-SD PTR queries to 224.0.0.251:5353, collects responses.
    ///
    /// This bypasses the Zeroconf NuGet package whose net48 build has broken
    /// multicast on Windows 11 (the DNS Client service binds 5353 first).
    /// Raw UdpClient with ReuseAddress works where Zeroconf does not.
    /// </summary>
    public class MdnsScanner : IDisposable
    {
        private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        private readonly Action<TraceLevel, string> _log;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly object _scanLock = new object();
        private volatile bool _scanning;
        private bool _disposed;

        public event EventHandler<MdnsService> ServiceFound;

        public MdnsScanner(Action<TraceLevel, string> log = null)
        {
            _log = log ?? ((_, __) => { });
        }

        /// <summary>
        /// Scan for mDNS services of the given types. Sends PTR queries for each
        /// service type and listens for responses for the scan duration.
        /// </summary>
        public async Task<List<MdnsService>> ScanAsync(
            IEnumerable<string> serviceTypes,
            int scanDurationMs = 4000,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MdnsScanner));

            lock (_scanLock)
            {
                if (_scanning)
                {
                    _log(TraceLevel.Warning, "mDNS scan already in progress");
                    return new List<MdnsService>();
                }
                _scanning = true;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token))
            {
                try
                {
                    return await ScanInternalAsync(serviceTypes.ToList(), scanDurationMs, linked.Token);
                }
                finally
                {
                    lock (_scanLock) { _scanning = false; }
                }
            }
        }

        /// <summary>
        /// Convenience overload for a single service type.
        /// </summary>
        public Task<List<MdnsService>> ScanAsync(
            string serviceType,
            int scanDurationMs = 4000,
            CancellationToken cancellationToken = default)
        {
            return ScanAsync(new[] { serviceType }, scanDurationMs, cancellationToken);
        }

        private async Task<List<MdnsService>> ScanInternalAsync(
            List<string> serviceTypes, int scanDurationMs, CancellationToken ct)
        {
            var types = serviceTypes.Select(NormalizeServiceType).ToList();
            var typesDisplay = string.Join(", ", types);
            _log(TraceLevel.Info, $"Starting mDNS scan for [{typesDisplay}] ({scanDurationMs}ms)");

            var results = new Dictionary<string, MdnsService>(StringComparer.OrdinalIgnoreCase);
            UdpClient udp = null;

            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                udp.JoinMulticastGroup(MdnsMulticast);

                // Also join on each active IPv4 interface for maximum reach
                try
                {
                    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (iface.OperationalStatus != OperationalStatus.Up) continue;
                        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var props = iface.GetIPProperties();
                        foreach (var addr in props.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                try
                                {
                                    udp.JoinMulticastGroup(MdnsMulticast, addr.Address);
                                    _log(TraceLevel.Verbose, $"Joined multicast on {addr.Address} ({iface.Name})");
                                }
                                catch { /* already joined or interface doesn't support it */ }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Verbose, $"Interface enumeration: {ex.Message}");
                }

                // Send PTR queries for each service type (send twice for reliability)
                var endpoint = new IPEndPoint(MdnsMulticast, MdnsPort);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    foreach (var type in types)
                    {
                        var query = BuildPtrQuery(type);
                        try
                        {
                            udp.Send(query, query.Length, endpoint);
                            _log(TraceLevel.Verbose, $"Sent PTR query for {type} (attempt {attempt + 1})");
                        }
                        catch (Exception ex)
                        {
                            _log(TraceLevel.Warning, $"Failed to send query for {type}: {ex.Message}");
                        }
                    }
                    if (attempt == 0)
                        await Task.Delay(250, ct);
                }

                // Listen for responses
                var deadline = DateTime.UtcNow.AddMilliseconds(scanDurationMs);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining.TotalMilliseconds <= 0) break;

                    // Set receive timeout so we don't block forever
                    udp.Client.ReceiveTimeout = Math.Max(100, (int)Math.Min(remaining.TotalMilliseconds, 500));

                    try
                    {
                        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                        var data = udp.Receive(ref remoteEp);

                        // Parse DNS response
                        var services = ParseDnsResponse(data, remoteEp.Address, types);
                        foreach (var svc in services)
                        {
                            if (!results.ContainsKey(svc.IPAddress))
                            {
                                results[svc.IPAddress] = svc;
                                _log(TraceLevel.Info, $"Found: {svc.Name} at {svc.IPAddress}:{svc.Port} ({svc.ServiceType})");
                                ServiceFound?.Invoke(this, svc);
                            }
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        // Normal — no data within timeout window, loop and check deadline
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            _log(TraceLevel.Verbose, $"Receive error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _log(TraceLevel.Info, _disposed ? "mDNS scan stopped (disposing)" : "mDNS scan cancelled");
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Warning, $"mDNS scan failed: {ex.Message}");
            }
            finally
            {
                try { udp?.Close(); } catch { }
            }

            var list = results.Values.ToList();
            _log(TraceLevel.Info, $"mDNS scan complete: {list.Count} service(s) found");
            return list;
        }

        // --- DNS packet building ---

        /// <summary>
        /// Build a DNS PTR query for a service type (e.g. "_devialet._tcp.local").
        /// Standard DNS wire format: header (12 bytes) + question section.
        /// </summary>
        private static byte[] BuildPtrQuery(string serviceType)
        {
            // Ensure it ends with .local
            if (!serviceType.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
                !serviceType.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
                serviceType += ".local";

            var name = EncodeDnsName(serviceType);

            // DNS header: ID=0, flags=0 (standard query), QDCOUNT=1
            var packet = new byte[12 + name.Length + 4];
            // ID = 0 (mDNS convention)
            // Flags = 0x0000 (standard query)
            packet[4] = 0; packet[5] = 1; // QDCOUNT = 1

            // Question: name + QTYPE(PTR=12) + QCLASS(IN=1, unicast-response bit set)
            Buffer.BlockCopy(name, 0, packet, 12, name.Length);
            int offset = 12 + name.Length;
            packet[offset] = 0; packet[offset + 1] = 12; // QTYPE = PTR
            packet[offset + 2] = 0x80; packet[offset + 3] = 1; // QCLASS = IN with unicast-response bit

            return packet;
        }

        /// <summary>
        /// Encode a domain name in DNS wire format (length-prefixed labels).
        /// "_devialet._tcp.local" → [9]_devialet[4]_tcp[5]local[0]
        /// </summary>
        private static byte[] EncodeDnsName(string name)
        {
            name = name.TrimEnd('.');
            var labels = name.Split('.');
            var result = new List<byte>();
            foreach (var label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                result.Add((byte)bytes.Length);
                result.AddRange(bytes);
            }
            result.Add(0); // root label
            return result.ToArray();
        }

        // --- DNS response parsing ---

        /// <summary>
        /// Parse a DNS response packet and extract service info.
        /// We look for PTR, SRV, A, and TXT records.
        /// </summary>
        private List<MdnsService> ParseDnsResponse(byte[] data, IPAddress senderIp, List<string> queryTypes)
        {
            var services = new List<MdnsService>();

            if (data.Length < 12) return services;

            try
            {
                // DNS header
                int flags = (data[2] << 8) | data[3];
                bool isResponse = (flags & 0x8000) != 0;
                if (!isResponse) return services; // Only process responses

                int qdcount = (data[4] << 8) | data[5];
                int ancount = (data[6] << 8) | data[7];
                int nscount = (data[8] << 8) | data[9];
                int arcount = (data[10] << 8) | data[11];

                int offset = 12;

                // Skip questions
                for (int i = 0; i < qdcount && offset < data.Length; i++)
                {
                    SkipDnsName(data, ref offset);
                    offset += 4; // QTYPE + QCLASS
                }

                // Collect all records (answers + authority + additional)
                var ptrRecords = new List<(string name, string target)>();
                var srvRecords = new Dictionary<string, (string host, ushort port)>(StringComparer.OrdinalIgnoreCase);
                var aRecords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var txtRecords = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

                int totalRecords = ancount + nscount + arcount;
                for (int i = 0; i < totalRecords && offset < data.Length; i++)
                {
                    string rName = ReadDnsName(data, ref offset);
                    if (offset + 10 > data.Length) break;

                    ushort rType = (ushort)((data[offset] << 8) | data[offset + 1]);
                    // ushort rClass = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                    // uint ttl = ...
                    ushort rdLength = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
                    offset += 10;

                    int rdEnd = offset + rdLength;
                    if (rdEnd > data.Length) break;

                    switch (rType)
                    {
                        case 12: // PTR
                            var ptrTarget = ReadDnsName(data, ref offset);
                            ptrRecords.Add((rName, ptrTarget));
                            break;

                        case 33: // SRV
                            if (rdLength >= 6)
                            {
                                // ushort priority = (ushort)((data[offset] << 8) | data[offset + 1]);
                                // ushort weight = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                                ushort port = (ushort)((data[offset + 4] << 8) | data[offset + 5]);
                                int srvOffset = offset + 6;
                                var srvHost = ReadDnsName(data, ref srvOffset);
                                srvRecords[rName] = (srvHost, port);
                            }
                            offset = rdEnd;
                            break;

                        case 1: // A
                            if (rdLength == 4)
                            {
                                var ip = $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";
                                aRecords[rName] = ip;
                            }
                            offset = rdEnd;
                            break;

                        case 16: // TXT
                            var props = new Dictionary<string, string>();
                            int txtOffset = offset;
                            while (txtOffset < rdEnd)
                            {
                                int len = data[txtOffset++];
                                if (len == 0 || txtOffset + len > rdEnd) break;
                                var txt = Encoding.UTF8.GetString(data, txtOffset, len);
                                txtOffset += len;
                                var eq = txt.IndexOf('=');
                                if (eq > 0)
                                    props[txt.Substring(0, eq)] = txt.Substring(eq + 1);
                            }
                            txtRecords[rName] = props;
                            offset = rdEnd;
                            break;

                        default:
                            offset = rdEnd;
                            break;
                    }
                }

                // Build services from PTR → SRV → A chain
                foreach (var (ptrName, instanceName) in ptrRecords)
                {
                    // Check if this PTR matches one of our query types
                    string matchedType = null;
                    foreach (var qt in queryTypes)
                    {
                        if (ptrName.IndexOf(qt.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            qt.IndexOf(ptrName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            NamesMatch(ptrName, qt))
                        {
                            matchedType = qt;
                            break;
                        }
                    }

                    // Resolve IP: SRV → A record chain, fall back to sender IP
                    string ip = senderIp.ToString();
                    ushort port = 80;
                    string hostName = instanceName;

                    if (srvRecords.TryGetValue(instanceName, out var srv))
                    {
                        port = srv.port;
                        hostName = srv.host;
                        if (aRecords.TryGetValue(srv.host, out var aIp))
                            ip = aIp;
                    }

                    // Extract friendly name from instance name
                    var friendlyName = ExtractFriendlyName(instanceName);

                    // Get TXT properties if available
                    var properties = new Dictionary<string, string>();
                    if (txtRecords.TryGetValue(instanceName, out var txtProps))
                        properties = txtProps;

                    services.Add(new MdnsService
                    {
                        Name = friendlyName,
                        IPAddress = ip,
                        Port = port,
                        HostName = hostName,
                        ServiceType = matchedType ?? ptrName,
                        Properties = properties
                    });
                }

                // If no PTR records but we got an A record from a known mDNS sender,
                // still record the host (some devices respond with just A records)
                if (services.Count == 0 && aRecords.Count > 0)
                {
                    foreach (var kvp in aRecords)
                    {
                        services.Add(new MdnsService
                        {
                            Name = ExtractFriendlyName(kvp.Key),
                            IPAddress = kvp.Value,
                            Port = 80,
                            HostName = kvp.Key,
                            ServiceType = queryTypes.FirstOrDefault() ?? ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Verbose, $"Parse error from {senderIp}: {ex.Message}");
            }

            return services;
        }

        private static bool NamesMatch(string a, string b)
        {
            return string.Equals(
                a.TrimEnd('.').Replace(".local", ""),
                b.TrimEnd('.').Replace(".local", ""),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read a DNS name from wire format, handling compression pointers.
        /// </summary>
        private static string ReadDnsName(byte[] data, ref int offset)
        {
            var parts = new List<string>();
            int jumps = 0;
            int savedOffset = -1;

            while (offset < data.Length && jumps < 20)
            {
                byte len = data[offset];
                if (len == 0)
                {
                    offset++;
                    break;
                }

                // Compression pointer (top 2 bits = 11)
                if ((len & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= data.Length) break;
                    int pointer = ((len & 0x3F) << 8) | data[offset + 1];
                    if (savedOffset < 0) savedOffset = offset + 2;
                    offset = pointer;
                    jumps++;
                    continue;
                }

                offset++;
                if (offset + len > data.Length) break;
                parts.Add(Encoding.UTF8.GetString(data, offset, len));
                offset += len;
            }

            if (savedOffset >= 0) offset = savedOffset;
            return string.Join(".", parts);
        }

        private static void SkipDnsName(byte[] data, ref int offset)
        {
            while (offset < data.Length)
            {
                byte len = data[offset];
                if (len == 0) { offset++; break; }
                if ((len & 0xC0) == 0xC0) { offset += 2; break; }
                offset += 1 + len;
            }
        }

        internal static string NormalizeServiceType(string serviceType)
        {
            if (string.IsNullOrEmpty(serviceType)) return serviceType;
            var result = serviceType.Trim().TrimEnd('.');
            if (result.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - ".local".Length).TrimEnd('.');
            return result;
        }

        internal static string ExtractFriendlyName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return displayName;
            int idx = displayName.IndexOf("._", StringComparison.Ordinal);
            if (idx > 0) return displayName.Substring(0, idx);
            if (displayName.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
                return displayName.Substring(0, displayName.Length - ".local.".Length);
            if (displayName.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                return displayName.Substring(0, displayName.Length - ".local".Length);
            return displayName;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _disposeCts.Cancel(); } catch { }
            _disposeCts.Dispose();
            ServiceFound = null;
        }
    }
}
