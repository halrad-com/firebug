# Firebug Changelog

## SsdpCore [0.5.0.2] - 2026-08-12

### Added
- **SsdpScanner** - One-shot M-SEARCH discovery sweep (the SSDP counterpart of `MdnsScanner`)
  - `ScanAsync(searchTargets, scanDurationMs, fetchDescriptions)` - Send M-SEARCH, collect responses for the window, return a deduplicated `List<SsdpDevice>`
  - Sends each search twice for UDP reliability; filters unsolicited NOTIFY chatter to the requested targets (`ssdp:all` matches everything)
  - `DeviceFound` event - Fires once per unique device at discovery time (before description fetch)
  - Results come back **named by default**: descriptions are fetched in parallel after the listen window
- **SsdpDescription** - Fetches and parses UPnP device description XML (the document behind `LOCATION`)
  - `FetchAsync(device, timeoutMs)` - Fills `FriendlyName`, `Manufacturer`, `ModelName`, `ModelDescription`, `PresentationUrl` on the device; never throws
  - Reads only the standard UPnP root-device elements, matched by local name - device-agnostic, vendor namespaces ignored
- **SsdpDevice.FetchDescriptionAsync()** - Instance convenience over `SsdpDescription.FetchAsync`

### Added (logging)
- **SsdpTrace** - Library-wide trace switch: callers set `SsdpTrace.Switch.Level` (or app.config, switch name `"SsdpCore"`) to control which levels reach their log callback. **Hard errors always log** - `TraceLevel.Error` bypasses the switch. Default Verbose = exact pre-switch behavior.

### Security
- Description fetches are **LAN-scoped**: `LOCATION` is device-controlled, so the fetch is refused unless the URL's host is an IP literal in private (RFC 1918), link-local, or unique-local space - DNS names are refused outright
- Response bodies capped at 256 KB (`MaxResponseContentBufferSize`) - a malicious `LOCATION` cannot make the library buffer an arbitrary payload
- Description-fetch redirects are followed **manually with per-hop LAN validation** (max 3 hops, one deadline for the whole chain): every redirect target passes the same LAN-scope gate as the original `LOCATION`, so a device can http->https itself or move ports, but a 302 can never leave the LAN (auto-follow stays off - it is the unvalidated path)
- Description enrichment is bounded: at most 64 devices per scan (truncation logged - SSDP responses are spoofable datagrams) through at most 8 concurrent fetches
- `SsdpMessage.BuildMSearch` rejects CR/LF in the search target (header injection)
- A throwing `DeviceFound` consumer handler is caught and attributed honestly instead of surfacing as a receive error

### Changed
- SsdpCore now makes its first HTTP use (`HttpClient`, in-box BCL - still no external packages): the description fetch. All discovery wire protocols remain pure UDP multicast.
- `SsdpScanner` constructor accepts an optional `localAddress` to scan from a specific NIC on multi-homed hosts (default remains the first non-virtual IPv4 interface)
- Description decoding reads raw bytes with BOM sniffing instead of trusting `Content-Type` - real devices (WiiM, GUPnP/Devialet) send quoted charsets that .NET Framework rejects as invalid

### Consumers
- MBXHub `POST /devices/endpoints/scan?protocol=ssdp` - LAN MediaRenderer sweep (WiiM/LinkPlay device scan in the WiiM charm)

---

## Firebug [0.2.0] - 2026-07-30

### Added
- **PortPicker** - Free-port selection to complement firewall/URL-ACL setup
  - `IsFree(port)` - Probe whether a port can be bound right now
  - `Pick(preferred)` - First free port at/above a preferred value
  - `PickPair(preferred)` - Low port of the first free adjacent pair (server + side-channel)
  - `Resolve(savedPort, preferred)` - Reuse a saved port if free, else pick fresh
- **LICENSE** - Apache License 2.0 (repo prepared for open source)

### Notes
- Persist the picked port so the URL ACL and firewall rule stay aligned across restarts.

---

## SsdpCore [0.5.0.1] - 2026-01-19

*(Changelog previously labeled SsdpCore entries 1.x; relabeled to match the
shipped assembly versions.)*

### Added
- **MdnsScanner** - Raw mDNS/DNS-SD one-shot sweep over UDP multicast (added within the 0.5.0.1 window; replaced an earlier Zeroconf-based attempt whose net48 multicast was broken on Windows 11)
- **SsdpClient** - M-SEARCH discovery for finding UPnP devices
  - `StartListening()` / `StopListening()` - Manage UDP listener
  - `SendSearch(searchTarget)` - Send M-SEARCH requests
  - `DeviceFound` event - Notifies when devices respond
  - `ShouldIncludeDevice()` - Override for custom filtering
- **SsdpServer** - NOTIFY advertising for announcing services
  - `Start()` / `Stop()` - Manage SSDP server lifecycle
  - `SendAlive()` - Manual NOTIFY alive broadcast
  - Responds to M-SEARCH requests automatically
  - Periodic NOTIFY alive with configurable MaxAge
- **SsdpMessage** - Message parsing and building utilities
  - `ParseHeader()` / `ParseHeaders()` - Extract SSDP headers
  - `BuildMSearch()` - Create M-SEARCH requests
  - `BuildNotifyAlive()` / `BuildNotifyByeBye()` - Create NOTIFY messages
  - `BuildSearchResponse()` - Create M-SEARCH responses
- **SsdpDevice** - Device data structure with SSDP fields
- **SsdpConstants** - Protocol constants (multicast address, port, headers)

### Technical
- Targets .NET Framework 4.8
- Pure .NET implementation, no external dependencies
- Supports multi-NIC binding for server mode

### Consumers
- PhantomBee (MBXCast) - UPnP device discovery
- MBXHub (future) - Service advertising

---

## Firebug [0.1.0] - 2026-01-19

### Added
- **FirebugManager** - Core library for Windows Firewall configuration
  - `IsFirewallEnabled()` - Check if Windows Firewall is on
  - `HasRule(name)` - Check if firewall rule exists
  - `HasUrlAcl(port)` - Check if URL ACL exists
  - `IsElevated()` - Check if running as admin
  - `AddTcpInboundRule()` / `AddUdpInboundRule()` - Add firewall rules
  - `AddUrlAcl()` - Add HTTP URL reservation
  - `RemoveRule()` / `RemoveUrlAcl()` - Remove rules
  - `OpenFirewallSettings()` - Open Windows Firewall UI
  - `GenerateScript()` - Generate batch file for manual config
- **Firebug CLI** - Standalone utility with admin manifest
  - `firebug add` - Add firewall rule and optional URL ACL
  - `firebug remove` - Remove firewall rule and URL ACL
  - `firebug check` - Check if rules exist
  - `firebug status` - Show firewall and elevation status
  - `firebug open` - Open Windows Firewall settings

### Technical
- Targets .NET Framework 4.8 (Windows-only, uses netsh)
- Admin manifest triggers UAC automatically
- Pattern consolidated from MBXHub, MBRC, MusicBee Chromecast utilities
