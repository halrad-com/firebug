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
    /// WS-Discovery server for advertising a device on the Windows Network folder.
    /// Mirrors SsdpServer: same socket-per-interface pattern, same threading, same lifecycle.
    /// </summary>
    public class WsdServer : IDisposable
    {
        private readonly Action<TraceLevel, string> _log;
        private readonly string _uuid;
        private readonly Func<string, string> _getXAddrsUrl;  // Takes local IP, returns XAddrs URL
        private readonly Random _random = new Random();

        private readonly long _instanceId;
        private long _messageNumber;

        private List<SocketContext> _sockets;
        private List<Timer> _notifyTimers;
        private bool _running;
        private bool _disposed;

        // Rate limiting: same approach as SsdpServer
        private readonly Dictionary<string, RateLimitEntry> _rateLimits = new Dictionary<string, RateLimitEntry>();
        private readonly object _rateLimitLock = new object();
        private const int MaxRequestsPerSecond = 5;
        private const int RateLimitWindowMs = 1000;

        /// <summary>
        /// Hello interval (half of a 30-minute max-age = 15 minutes)
        /// </summary>
        public TimeSpan NotifyInterval { get; set; } = TimeSpan.FromSeconds(900);

        /// <summary>
        /// Initial burst interval before switching to regular timer
        /// </summary>
        public TimeSpan InitialBurstInterval { get; set; } = TimeSpan.FromSeconds(30);

        private class RateLimitEntry
        {
            public int Count;
            public long WindowStart;
        }

        /// <summary>
        /// Create a new WS-Discovery server.
        /// </summary>
        /// <param name="uuid">Device UUID (same one used for SSDP, without "uuid:" prefix)</param>
        /// <param name="getXAddrsUrl">Function: localIp → XAddrs URL (e.g. "http://{ip}:{port}/wsd")</param>
        /// <param name="log">Optional logging callback</param>
        public WsdServer(
            string uuid,
            Func<string, string> getXAddrsUrl,
            Action<TraceLevel, string> log = null)
        {
            _uuid = uuid ?? throw new ArgumentNullException(nameof(uuid));
            _getXAddrsUrl = getXAddrsUrl ?? throw new ArgumentNullException(nameof(getXAddrsUrl));
            _log = log ?? ((_, __) => { });
            _instanceId = Environment.TickCount & 0x7FFFFFFF;
        }

        /// <summary>
        /// Start advertising via WS-Discovery (sends Hello, listens for Probes).
        /// </summary>
        public void Start()
        {
            if (_running) return;

            _log(TraceLevel.Info, "Starting WS-Discovery server");

            var addresses = GetLocalAddresses();
            if (addresses.Count == 0)
            {
                _log(TraceLevel.Warning, "No network interfaces found for WS-Discovery");
                return;
            }

            _sockets = new List<SocketContext>();
            _notifyTimers = new List<Timer>();
            _running = true;
            var multicastGroup = IPAddress.Parse(WsdConstants.MulticastAddress);

            foreach (var address in addresses)
            {
                try
                {
                    var context = new SocketContext { Address = address };

                    // Listener socket for receiving Probes
                    var listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    listenerSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, WsdConstants.MulticastTtl);
                    listenerSocket.Bind(new IPEndPoint(address, WsdConstants.Port));
                    listenerSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(multicastGroup, address));
                    context.ListenerSocket = listenerSocket;

                    // Notify socket for sending Hello/Bye
                    var notifySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    notifySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    notifySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    notifySocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, WsdConstants.MulticastTtl);
                    notifySocket.Bind(new IPEndPoint(address, WsdConstants.Port));
                    context.NotifySocket = notifySocket;

                    _sockets.Add(context);

                    // Start Probe listener thread
                    var thread = new Thread(ListenForProbes)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = $"WSD-Listen-{address}"
                    };
                    context.ListenerThread = thread;
                    thread.Start(context);

                    _log(TraceLevel.Info, $"WS-Discovery server bound to {address}");
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to bind WS-Discovery to {address}: {ex.Message}");
                }
            }

            // Send initial Bye (clears stale entries) then Hello burst
            foreach (var ctx in _sockets)
            {
                try
                {
                    SendBye(ctx);
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to send initial Bye: {ex.Message}");
                }

                // Start periodic Hello timer
                var timer = new Timer(OnHelloTimer, ctx, (int)InitialBurstInterval.TotalMilliseconds, (int)NotifyInterval.TotalMilliseconds);
                _notifyTimers.Add(timer);
            }

            // Startup burst: 3x Hello at random intervals
            SendStartupBurst();
        }

        /// <summary>
        /// Stop advertising (sends Bye, stops listening).
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _log(TraceLevel.Info, "Stopping WS-Discovery server");
            _running = false;

            // Dispose timers
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

            // Send Bye and close sockets
            if (_sockets != null)
            {
                foreach (var ctx in _sockets)
                {
                    try { SendBye(ctx); }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error sending Bye: {ex.Message}");
                    }

                    try { ctx.ListenerSocket?.Close(); }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error closing listener socket: {ex.Message}");
                    }

                    try { ctx.NotifySocket?.Close(); }
                    catch (Exception ex)
                    {
                        _log(TraceLevel.Warning, $"Error closing notify socket: {ex.Message}");
                    }
                }

                // Wait for listener threads
                foreach (var ctx in _sockets)
                {
                    if (ctx.ListenerThread != null && ctx.ListenerThread.IsAlive)
                    {
                        if (!ctx.ListenerThread.Join(2000))
                        {
                            _log(TraceLevel.Warning, $"Listener thread for {ctx.Address} did not terminate in time");
                        }
                    }
                }

                _sockets.Clear();
            }

            // Clear rate limits
            lock (_rateLimitLock)
            {
                _rateLimits.Clear();
            }

            _log(TraceLevel.Info, "WS-Discovery server stopped");
        }

        /// <summary>
        /// Send Hello messages on all interfaces immediately.
        /// </summary>
        public void SendHello()
        {
            if (!_running || _sockets == null) return;

            foreach (var ctx in _sockets)
            {
                try
                {
                    SendHelloMessage(ctx);
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Failed to send Hello: {ex.Message}");
                }
            }
        }

        private void SendStartupBurst()
        {
            _log(TraceLevel.Info, "Sending WSD startup Hello burst (1/3)");
            SendHello();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(1000 + _random.Next(1000));
                    if (!_running) return;
                    _log(TraceLevel.Info, "Sending WSD startup Hello burst (2/3)");
                    SendHello();

                    Thread.Sleep(2000 + _random.Next(1000));
                    if (!_running) return;
                    _log(TraceLevel.Info, "Sending WSD startup Hello burst (3/3)");
                    SendHello();
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Startup burst error: {ex.Message}");
                }
            });
        }

        private void OnHelloTimer(object state)
        {
            if (!_running) return;

            var ctx = (SocketContext)state;
            try
            {
                SendHelloMessage(ctx);
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Warning, $"Hello timer error: {ex.Message}");
            }
        }

        private void SendHelloMessage(SocketContext ctx)
        {
            var xAddrs = _getXAddrsUrl(ctx.Address.ToString());
            var msgNum = Interlocked.Increment(ref _messageNumber);

            var message = WsdMessage.BuildHello(_uuid, xAddrs, _instanceId, msgNum);
            var bytes = Encoding.UTF8.GetBytes(message);
            var multicastEp = new IPEndPoint(IPAddress.Parse(WsdConstants.MulticastAddress), WsdConstants.Port);

            ctx.NotifySocket.SendTo(bytes, multicastEp);
            _log(TraceLevel.Verbose, $"Sent WSD Hello on {ctx.Address}");
        }

        private void SendBye(SocketContext ctx)
        {
            var msgNum = Interlocked.Increment(ref _messageNumber);

            var message = WsdMessage.BuildBye(_uuid, _instanceId, msgNum);
            var bytes = Encoding.UTF8.GetBytes(message);
            var multicastEp = new IPEndPoint(IPAddress.Parse(WsdConstants.MulticastAddress), WsdConstants.Port);

            ctx.NotifySocket.SendTo(bytes, multicastEp);
            _log(TraceLevel.Verbose, $"Sent WSD Bye on {ctx.Address}");
        }

        private void ListenForProbes(object state)
        {
            var ctx = (SocketContext)state;
            var buffer = new byte[4096];  // SOAP messages are larger than SSDP
            var host = ctx.Address.ToString();

            _log(TraceLevel.Info, $"WS-Discovery listening on {host}");

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

                var message = Encoding.UTF8.GetString(buffer, 0, length);

                if (!WsdMessage.IsProbe(message)) continue;

                try
                {
                    ProcessProbe(ctx, message, remoteEp);
                }
                catch (Exception ex)
                {
                    _log(TraceLevel.Warning, $"Error processing Probe: {ex.Message}");
                }
            }

            _log(TraceLevel.Info, $"WS-Discovery listener stopped on {host}");
        }

        private void ProcessProbe(SocketContext ctx, string message, EndPoint remoteEp)
        {
            var sourceIp = (remoteEp as IPEndPoint)?.Address?.ToString() ?? "unknown";
            if (!CheckRateLimit(sourceIp))
            {
                _log(TraceLevel.Warning, $"Rate limit exceeded for {sourceIp}, dropping Probe");
                return;
            }

            var relatesToId = WsdMessage.ParseMessageId(message);
            if (string.IsNullOrEmpty(relatesToId))
            {
                _log(TraceLevel.Verbose, "Probe without MessageID, ignoring");
                return;
            }

            // Random delay 0-500ms (similar to SSDP MX handling)
            Thread.Sleep(_random.Next(500));

            var xAddrs = _getXAddrsUrl(ctx.Address.ToString());
            var msgNum = Interlocked.Increment(ref _messageNumber);

            var response = WsdMessage.BuildProbeMatch(_uuid, xAddrs, _instanceId, msgNum, relatesToId);
            var bytes = Encoding.UTF8.GetBytes(response);

            // ProbeMatch is unicast back to sender
            ctx.ListenerSocket.SendTo(bytes, remoteEp);

            _log(TraceLevel.Verbose, $"Sent ProbeMatch to {remoteEp} (RelatesTo: {relatesToId})");
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

        private bool CheckRateLimit(string sourceIp)
        {
            var now = Environment.TickCount;

            lock (_rateLimitLock)
            {
                if (_rateLimits.TryGetValue(sourceIp, out var entry))
                {
                    if (now - entry.WindowStart < RateLimitWindowMs)
                    {
                        entry.Count++;
                        if (entry.Count > MaxRequestsPerSecond)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        entry.WindowStart = now;
                        entry.Count = 1;
                    }
                }
                else
                {
                    _rateLimits[sourceIp] = new RateLimitEntry { Count = 1, WindowStart = now };
                }

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
