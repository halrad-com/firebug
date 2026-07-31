# Fireants Changelog

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

## SsdpCore [1.0.0] - 2026-01-19

### Added
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
