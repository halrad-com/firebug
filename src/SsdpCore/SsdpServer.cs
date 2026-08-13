using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SsdpCore
{
    /// <summary>
    /// SSDP server for advertising a device via NOTIFY messages and responding to M-SEARCH requests.
    /// This is the "announce our presence on the network" side of SSDP.
    /// </summary>
    public class SsdpServer : IDisposable
    {
        private readonly Action<TraceLevel, string> _log;
        private readonly string _uuid;
        private readonly string _deviceType;
        private readonly Func<string, string> _getLocationUrl;  // Takes local IP, returns full URL
        private readonly string _serverString;
        private readonly List<string> _additionalTypes;
        private readonly Random _random = new Random();

        private List<SocketContext> _sockets;
        private List<Timer> _notifyTimers;
        private bool _running;
        private bool _disposed;

        // Rate limiting: track requests per source IP
        private readonly Dictionary<string, RateLimitEntry> _rateLimits = new Dictionary<string, RateLimitEntry>();
        private readonly object _rateLimitLock = new object();
        private const int MaxRequestsPerSecond = 5;
        private const int RateLimitWindowMs = 1000;

        // Explicit, user-curated banned peers. M-SEARCH from these is dropped silently
        // before MX sleep / response composition. No heuristics, no auto-add.
        private HashSet<IPAddress> _bannedPeers = new HashSet<IPAddress>();
        private readonly object _bannedPeersLock = new object();

        private class RateLimitEntry
        {
            public int Count;
            public int WindowStart;
        }

        /// <summary>
        /// Cache-control max-age in seconds (default: 30 minutes)
        /// </summary>
        public int MaxAge { get; set; } = SsdpConstants.DefaultMaxAge;

        /// <summary>
        /// Interval between NOTIFY alive messages (should be less than MaxAge)
        /// Default is MaxAge / 2 to ensure at least 2 advertisements per expiry period
        /// </summary>
        public TimeSpan NotifyInterval => TimeSpan.FromSeconds(MaxAge / 2);

        /// <summary>
        /// Initial burst interval for faster discovery at startup (default: 30 seconds)
        /// </summary>
        public TimeSpan InitialBurstInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Create a new SSDP server for advertising a device
        /// </summary>
        /// <param name="uuid">Device UUID (without "uuid:" prefix)</param>
        /// <param name="deviceType">Primary device type (e.g., "urn:halrad-com:device:MBXHub:1")</param>
        /// <param name="getLocationUrl">Function that returns the device description URL for a given local IP</param>
        /// <param name="serverString">SERVER header value (use SsdpMessage.BuildServerString)</param>
        /// <param name="additionalTypes">Additional service types to advertise (optional)</param>
        /// <param name="log">Optional logging callback</param>
        public SsdpServer(
            string uuid,
            string deviceType,
            Func<string, string> getLocationUrl,
            string serverString,
            IEnumerable<string> additionalTypes = null,
            Action<TraceLevel, string> log = null)
        {
            _uuid = uuid ?? throw new ArgumentNullException(nameof(uuid));
            _deviceType = deviceType ?? throw new ArgumentNullException(nameof(deviceType));
            _getLocationUrl = getLocationUrl ?? throw new ArgumentNullException(nameof(getLocationUrl));
            _serverString = serverString ?? throw new ArgumentNullException(nameof(serverString));
            _additionalTypes = additionalTypes != null ? new List<string>(additionalTypes) : new List<string>();
            _log = SsdpTrace.Wrap(log);
        }

        /// <summary>
        /// Start advertising the device (sends NOTIFY, listens for M-SEARCH)
        /// </summary>
        public void Start()
        {
            if (_running) return;

            _log(TraceLevel.Info, "Starting SSDP server");

            var addresses = GetLocalAddresses();
            if (addresses.Count == 0)
            {
                _log(TraceLevel.Warning, "No network interfaces found for SSDP");
                return;
            }

            _sockets = new List<SocketContext>();
            _notifyTimers = new List<Timer>();
            _running = true;  // Set BEFORE starting threads to avoid race condition
            var multicastGroup = IPAddress.Parse(SsdpConstants.MulticastAddress);

            foreach (var address in addresses)
            {
                try
                {
                    var context = new SocketContext { Address = address };

                    // Create listener socket for receiving M-SEARCH requests
                    var listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    listenerSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, SsdpConstants.MulticastTtl);
                    listenerSocket.Bind(new IPEndPoint(address, SsdpConstants.Port));
                    listenerSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(multicastGroup, address));
                    context.ListenerSocket = listenerSocket;

                    // Create notify socket for sending NOTIFY messages
                    var notifySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    notifySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    notifySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    notifySocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, SsdpConstants.MulticastTtl);
                    notifySocket.Bind(new IPEndPoint(address, SsdpConstants.Port));
                    context.NotifySocket = notifySocket;

                    _sockets.Add(context);

                    // Start listener thread
                    var thread = new Thread(ListenForMSearch)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = $"SSDP-Listen-{address}"
                    };
                    context.ListenerThread = thread;
                    thread.Start(context);

                    _log(TraceLevel.Info, $"SSDP server bound to {address}");
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to bind SSDP to {address}: {ex.Message}");
                }
            }

            // Send initial byebye (clears stale entries) then alive burst
            foreach (var ctx in _sockets)
            {
                try
                {
                    SendNotify(ctx, isAlive: false);
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to send initial byebye: {ex.Message}");
                }

                // Start periodic NOTIFY timer
                var timer = new Timer(OnNotifyTimer, ctx, (int)InitialBurstInterval.TotalMilliseconds, (int)NotifyInterval.TotalMilliseconds);
                _notifyTimers.Add(timer);
            }

            // Send startup burst of NOTIFY alive messages
            // UPnP spec recommends sending 2-3 announcements at random intervals
            // This helps ensure Windows and other clients discover us immediately
            SendStartupBurst();
        }

        /// <summary>
        /// Stop advertising the device (sends byebye, stops listening)
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _log(TraceLevel.Info, "Stopping SSDP server");
            _running = false;

            // Dispose timers first to stop new notifications
            if (_notifyTimers != null)
            {
                foreach (var timer in _notifyTimers)
                {
                    try { timer?.Dispose(); }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error disposing timer: {ex.Message}");
                    }
                }
                _notifyTimers.Clear();
            }

            // Send byebye and close sockets
            if (_sockets != null)
            {
                foreach (var ctx in _sockets)
                {
                    // Try to send byebye notification
                    try
                    {
                        SendNotify(ctx, isAlive: false);
                    }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error sending byebye: {ex.Message}");
                    }

                    // Close sockets to unblock listeners
                    try
                    {
                        ctx.ListenerSocket?.Close();
                    }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error closing listener socket: {ex.Message}");
                    }

                    try
                    {
                        ctx.NotifySocket?.Close();
                    }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error closing notify socket: {ex.Message}");
                    }
                }

                // Wait for all listener threads to terminate (with timeout)
                foreach (var ctx in _sockets)
                {
                    if (ctx.ListenerThread != null && ctx.ListenerThread.IsAlive)
                    {
                        if (!ctx.ListenerThread.Join(2000))
                        {
                            _log(TraceLevel.Warning, $"Listener thread for {ctx.Address} did not terminate in time");
                            // Thread will terminate eventually when socket operations fail
                        }
                    }
                }

                _sockets.Clear();
            }

            // Clear rate limit tracking
            lock (_rateLimitLock)
            {
                _rateLimits.Clear();
            }

            _log(TraceLevel.Info, "SSDP server stopped");
        }

        /// <summary>
        /// Send NOTIFY alive messages immediately (manual refresh)
        /// </summary>
        public void SendAlive()
        {
            if (!_running || _sockets == null) return;

            foreach (var ctx in _sockets)
            {
                try
                {
                    SendNotify(ctx, isAlive: true);
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to send NOTIFY alive: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Send a burst of NOTIFY alive messages at startup to ensure discovery.
        /// UPnP spec recommends 2-3 announcements at random intervals.
        /// </summary>
        private void SendStartupBurst()
        {
            // Send initial announcement immediately
            _log(TraceLevel.Info, "Sending startup NOTIFY burst (1/3)");
            SendAlive();

            // Schedule additional announcements on background thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Second announcement after 1-2 seconds
                    Thread.Sleep(1000 + _random.Next(1000));
                    if (!_running) return;
                    _log(TraceLevel.Info, "Sending startup NOTIFY burst (2/3)");
                    SendAlive();

                    // Third announcement after another 2-3 seconds
                    Thread.Sleep(2000 + _random.Next(1000));
                    if (!_running) return;
                    _log(TraceLevel.Info, "Sending startup NOTIFY burst (3/3)");
                    SendAlive();
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Startup burst error: {ex.Message}");
                }
            });
        }

        private void OnNotifyTimer(object state)
        {
            if (!_running) return;

            var ctx = (SocketContext)state;
            try
            {
                SendNotify(ctx, isAlive: true);
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Warning, $"NOTIFY timer error: {ex.Message}");
            }
        }

        private void SendNotify(SocketContext ctx, bool isAlive)
        {
            var host = ctx.Address.ToString();
            var location = _getLocationUrl(host);

            // Announce root device
            SendNotifyMessage(ctx.NotifySocket, host, "upnp:rootdevice",
                SsdpMessage.BuildUsn(_uuid, "upnp:rootdevice"), location, isAlive);

            // Announce UUID
            SendNotifyMessage(ctx.NotifySocket, host, $"uuid:{_uuid}",
                $"uuid:{_uuid}", location, isAlive);

            // Announce device type
            SendNotifyMessage(ctx.NotifySocket, host, _deviceType,
                SsdpMessage.BuildUsn(_uuid, _deviceType), location, isAlive);

            // Announce additional types (services)
            foreach (var serviceType in _additionalTypes)
            {
                SendNotifyMessage(ctx.NotifySocket, host, serviceType,
                    SsdpMessage.BuildUsn(_uuid, serviceType), location, isAlive);
            }
        }

        private void SendNotifyMessage(Socket socket, string host, string nt, string usn, string location, bool isAlive)
        {
            try
            {
                string message = isAlive
                    ? SsdpMessage.BuildNotifyAlive(location, nt, usn, _serverString, MaxAge)
                    : SsdpMessage.BuildNotifyByeBye(nt, usn);

                var bytes = Encoding.ASCII.GetBytes(message);
                var multicastEp = new IPEndPoint(IPAddress.Parse(SsdpConstants.MulticastAddress), SsdpConstants.Port);
                socket.SendTo(bytes, multicastEp);

                _log(TraceLevel.Verbose, $"Sent NOTIFY {(isAlive ? "alive" : "byebye")} for {nt}");
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Warning, $"SendNotifyMessage failed: {ex.Message}");
            }

            Thread.Sleep(50);  // Brief pause between messages per UPnP spec
        }

        private void ListenForMSearch(object state)
        {
            var ctx = (SocketContext)state;
            var buffer = new byte[1024];
            var host = ctx.Address.ToString();

            _log(TraceLevel.Info, $"SSDP listening on {host}");

            while (_running)
            {
                EndPoint remoteEp = new IPEndPoint(0, 0);
                int length;

                try
                {
                    length = ctx.ListenerSocket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remoteEp);
                }
                catch
                {
                    break;
                }

                if (length == 0) continue;

                var message = Encoding.ASCII.GetString(buffer, 0, length);

                // Only process M-SEARCH requests
                if (!SsdpMessage.IsMSearch(message)) continue;

                // Process on thread pool to avoid blocking the listener
                // (ProcessMSearch uses Thread.Sleep for SSDP MX delay)
                var capturedMessage = message;
                var capturedRemoteEp = remoteEp;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        ProcessMSearch(ctx, capturedMessage, capturedRemoteEp, host);
                    }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error processing M-SEARCH: {ex.Message}");
                    }
                });
            }

            _log(TraceLevel.Info, $"SSDP listener stopped on {host}");
        }

        private void ProcessMSearch(SocketContext ctx, string message, EndPoint remoteEp, string host)
        {
            // Banned peers: drop before any work — no MX sleep, no parse, no response.
            var sourceAddr = (remoteEp as IPEndPoint)?.Address;
            if (sourceAddr != null && IsBanned(sourceAddr))
            {
                _log(TraceLevel.Verbose, $"Ignored M-SEARCH from banned peer {sourceAddr}");
                return;
            }

            // Rate limiting: check if this source IP is sending too many requests
            var sourceIp = sourceAddr?.ToString() ?? "unknown";
            if (!CheckRateLimit(sourceIp))
            {
                _log(TraceLevel.Warning, $"Rate limit exceeded for {sourceIp}, dropping M-SEARCH");
                return;
            }

            var headers = SsdpMessage.ParseHeaders(message);

            // Validate required headers
            if (!headers.TryGetValue("MAN", out var man) ||
                !man.Equals("\"ssdp:discover\"", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!headers.TryGetValue("ST", out var st))
            {
                return;
            }

            // Parse MX for response delay
            int mx = 1;
            if (headers.TryGetValue("MX", out var mxStr) && int.TryParse(mxStr, out var parsedMx) && parsedMx > 0)
            {
                mx = Math.Min(parsedMx, 5);  // Cap at 5 seconds
            }

            var location = _getLocationUrl(host);
            var uuid = $"uuid:{_uuid}";

            // Determine what to respond with
            if (st.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase))
            {
                // Respond with everything
                Thread.Sleep(_random.Next(mx * 100));
                SendSearchResponse(ctx.ListenerSocket, remoteEp, host, "upnp:rootdevice",
                    SsdpMessage.BuildUsn(_uuid, "upnp:rootdevice"), location);
                SendSearchResponse(ctx.ListenerSocket, remoteEp, host, uuid, uuid, location);
                SendSearchResponse(ctx.ListenerSocket, remoteEp, host, _deviceType,
                    SsdpMessage.BuildUsn(_uuid, _deviceType), location);

                foreach (var serviceType in _additionalTypes)
                {
                    SendSearchResponse(ctx.ListenerSocket, remoteEp, host, serviceType,
                        SsdpMessage.BuildUsn(_uuid, serviceType), location);
                }
            }
            else if (st.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
                     st.Equals(_deviceType, StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(_random.Next(mx * 100));
                SendSearchResponse(ctx.ListenerSocket, remoteEp, host, st,
                    SsdpMessage.BuildUsn(_uuid, st), location);
            }
            else if (st.Equals(uuid, StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(_random.Next(mx * 100));
                SendSearchResponse(ctx.ListenerSocket, remoteEp, host, st, uuid, location);
            }
            else
            {
                // Check additional types
                foreach (var serviceType in _additionalTypes)
                {
                    if (st.Equals(serviceType, StringComparison.OrdinalIgnoreCase))
                    {
                        Thread.Sleep(_random.Next(mx * 100));
                        SendSearchResponse(ctx.ListenerSocket, remoteEp, host, st,
                            SsdpMessage.BuildUsn(_uuid, st), location);
                        break;
                    }
                }
            }
        }

        private void SendSearchResponse(Socket socket, EndPoint remoteEp, string host, string st, string usn, string location)
        {
            try
            {
                var message = SsdpMessage.BuildSearchResponse(location, st, usn, _serverString, MaxAge);
                var bytes = Encoding.ASCII.GetBytes(message);
                socket.SendTo(bytes, remoteEp);

                _log(TraceLevel.Verbose, $"Sent M-SEARCH response for {st} to {remoteEp}");
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Warning, $"Failed to send search response: {ex.Message}");
            }
        }

        private List<IPAddress> GetLocalAddresses()
        {
            var addresses = new List<IPAddress>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            addresses.Add(addr.Address);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Error, $"Failed to enumerate network interfaces: {ex.Message}");
            }

            return addresses;
        }

        /// <summary>
        /// Check if a source IP is within rate limits.
        /// Returns true if request is allowed, false if rate limit exceeded.
        /// </summary>
        private bool CheckRateLimit(string sourceIp)
        {
            var now = Environment.TickCount;

            lock (_rateLimitLock)
            {
                if (_rateLimits.TryGetValue(sourceIp, out var entry))
                {
                    // Check if we're still in the same window
                    if (now - entry.WindowStart < RateLimitWindowMs)
                    {
                        entry.Count++;
                        if (entry.Count > MaxRequestsPerSecond)
                        {
                            return false; // Rate limit exceeded
                        }
                    }
                    else
                    {
                        // New window, reset count
                        entry.WindowStart = now;
                        entry.Count = 1;
                    }
                }
                else
                {
                    // First request from this IP
                    _rateLimits[sourceIp] = new RateLimitEntry { Count = 1, WindowStart = now };
                }

                // Cleanup old entries periodically (every ~100 requests)
                if (_rateLimits.Count > 100)
                {
                    var expiredKeys = new List<string>();
                    foreach (var kvp in _rateLimits)
                    {
                        if (now - kvp.Value.WindowStart > RateLimitWindowMs * 10)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                    }
                    foreach (var key in expiredKeys)
                    {
                        _rateLimits.Remove(key);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Replace the banned-peer set. M-SEARCH from these addresses is dropped silently.
        /// Pass an empty collection (or null) to clear. Unparseable entries are ignored.
        /// </summary>
        public void SetBannedPeers(IEnumerable<string> ips)
        {
            var set = new HashSet<IPAddress>();
            if (ips != null)
            {
                foreach (var s in ips)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (IPAddress.TryParse(s.Trim(), out var ip))
                        set.Add(ip);
                }
            }
            lock (_bannedPeersLock) { _bannedPeers = set; }
            _log(TraceLevel.Info, $"SSDP banned-peer list updated: {set.Count} entries");
        }

        private bool IsBanned(IPAddress addr)
        {
            if (addr == null) return false;
            lock (_bannedPeersLock) { return _bannedPeers.Contains(addr); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private class SocketContext
        {
            public IPAddress Address;
            public Socket ListenerSocket;
            public Socket NotifySocket;
            public Thread ListenerThread;
        }
    }
}
