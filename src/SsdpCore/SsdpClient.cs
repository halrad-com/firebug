using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SsdpCore
{
    /// <summary>
    /// SSDP client for discovering devices via M-SEARCH requests.
    /// This is the "find devices on the network" side of SSDP.
    /// </summary>
    public class SsdpClient : IDisposable
    {
        private readonly Action<TraceLevel, string> _log;
        private readonly IPAddress _localAddress;
        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private bool _disposed;

        /// <summary>
        /// Raised when an SSDP response is received (before filtering)
        /// </summary>
        public event EventHandler<SsdpDevice> DeviceFound;

        /// <summary>
        /// Create a new SSDP client
        /// </summary>
        /// <param name="log">Optional logging callback</param>
        /// <param name="localAddress">Local address to bind to (null = IPAddress.Any)</param>
        public SsdpClient(Action<TraceLevel, string> log = null, IPAddress localAddress = null)
        {
            _log = log ?? ((_, __) => { });
            _localAddress = localAddress ?? IPAddress.Any;
        }

        /// <summary>
        /// Start listening for SSDP responses and NOTIFY messages
        /// </summary>
        public void StartListening()
        {
            if (_cts != null)
            {
                _log(TraceLevel.Warning, "SsdpClient already listening");
                return;
            }

            _log(TraceLevel.Info, $"Starting SSDP client (bind: {_localAddress})");

            _cts = new CancellationTokenSource();

            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(_localAddress, 0));
                _udpClient.JoinMulticastGroup(
                    IPAddress.Parse(SsdpConstants.MulticastAddress),
                    _localAddress);

                _listenTask = ListenAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Error, $"Failed to start SSDP client: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stop listening for SSDP messages
        /// </summary>
        public void StopListening()
        {
            if (_cts == null) return;

            _log(TraceLevel.Info, "Stopping SSDP client");

            _cts.Cancel();

            try
            {
                _listenTask?.Wait(1000);
            }
            catch (AggregateException) { }

            _udpClient?.Close();
            _udpClient = null;
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// Send an M-SEARCH request for the specified search target
        /// </summary>
        /// <param name="searchTarget">The search target (e.g., "ssdp:all", "urn:schemas-upnp-org:service:AVTransport:1")</param>
        /// <param name="mx">Maximum wait time in seconds</param>
        public void SendSearch(string searchTarget, int mx = 3)
        {
            if (_udpClient == null)
            {
                _log(TraceLevel.Warning, "Cannot send search: client not listening");
                return;
            }

            var message = SsdpMessage.BuildMSearch(searchTarget, mx);
            var bytes = Encoding.UTF8.GetBytes(message);
            var endpoint = new IPEndPoint(IPAddress.Parse(SsdpConstants.MulticastAddress), SsdpConstants.Port);

            try
            {
                _udpClient.Send(bytes, bytes.Length, endpoint);
                _log(TraceLevel.Info, $"SSDP search sent for {searchTarget}");
            }
            catch (Exception ex)
            {
                _log(TraceLevel.Error, $"Failed to send SSDP search: {ex.Message}");
            }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            _log(TraceLevel.Info, "Listening for SSDP responses");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    var message = Encoding.UTF8.GetString(result.Buffer);

                    // Process search responses (HTTP/1.1 200 OK) and NOTIFY messages
                    if (SsdpMessage.IsSearchResponse(message) || SsdpMessage.IsNotify(message))
                    {
                        ProcessResponse(message, result.RemoteEndPoint.Address);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _log(TraceLevel.Warning, $"Error receiving SSDP: {ex.Message}");
                    }
                }
            }

            _log(TraceLevel.Info, "SSDP listener stopped");
        }

        private void ProcessResponse(string message, IPAddress address)
        {
            var device = SsdpDevice.FromSsdpResponse(message, address);
            if (device == null)
            {
                _log(TraceLevel.Verbose, "Received SSDP message without LOCATION header");
                return;
            }

            // Check for byebye (device leaving)
            var nts = SsdpMessage.ParseHeader(message, SsdpConstants.HeaderNTS);
            if (nts?.Equals(SsdpConstants.NtsByeBye, StringComparison.OrdinalIgnoreCase) == true)
            {
                _log(TraceLevel.Info, $"Device leaving: {device.Location}");
                // Could add DeviceLost event here if needed
                return;
            }

            // Let derived classes filter
            if (!ShouldIncludeDevice(device))
            {
                return;
            }

            _log(TraceLevel.Info, $"Found device at {device.Location}");
            DeviceFound?.Invoke(this, device);
        }

        /// <summary>
        /// Override in derived class to filter discovered devices.
        /// Return true to include the device, false to exclude.
        /// </summary>
        protected virtual bool ShouldIncludeDevice(SsdpDevice device)
        {
            return true;
        }

        /// <summary>
        /// Returns the first non-loopback IPv4 address on an active network interface.
        /// Useful for binding the SSDP client to the correct NIC on multi-homed machines.
        /// Returns null if no suitable address is found.
        /// </summary>
        public static IPAddress GetPreferredLocalAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    // Skip virtual adapters (Hyper-V, WSL, VMware, VPN tap)
                    var desc = ni.Description.ToLowerInvariant();
                    if (desc.Contains("hyper-v") || desc.Contains("virtual") ||
                        desc.Contains("vmware") || desc.Contains("vethernet") ||
                        desc.Contains("wsl"))
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            return addr.Address;
                    }
                }
            }
            catch { /* fall through */ }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopListening();
        }
    }
}
