# Firebug Changelog

> **SsdpCore version labels:** entries use shipped assembly-file versions (0.5.0.x).
> Earlier drafts of this file - and some pre-relabel commit messages - referred to
> the two SsdpCore entries below as 1.1.0 / 1.0.0.

## Firebug.Cli [0.4.10.0] - 2026-08-13

### Added
- **`firebug pick`** - free-port selection from the command line, the worked example for `PortPicker`
  - `--preferred <port>` (default 8000), `--saved <port>` (reuse a persisted port when still free), `--pair` (adjacent pair for server + side-channel)
  - Prints a parseable `PORT: <n>` (and `SIDE: <n+1>` for pairs); exit 0 = the printed port is bindable right now, 1 = nothing free
  - Pure and unelevated - persist the port, then reserve it
- **`firebug reserve`** - the authorize half: firewall rule **and** URL ACL for a known port in one verb
  - `--name` + `--port` (or `--pick` with the pick flags for the combined pick-then-reserve flow: the pick runs unelevated so `PORT:` prints in your console, then the concrete port is reserved under UAC)
  - `--pair` reserves both ports; `--no-urlacl` for rule-only; UDP reservations are rule-only automatically (URL ACLs are an http.sys concept)
  - Idempotent - re-reserving replaces the app's rules instead of stacking duplicates; bad args fail fast **before** any UAC prompt
- **Interactive console** - run `firebug` bare in a terminal for a prompt with **Tab verb completion**, ghost-text suggestions, and Up/Down history; `quit` to leave. Redirected/scripted invocations keep the old usage + exit 1 contract. The line editor is a pure, unit-tested state machine (`LineEditorLogic`) driving a dumb painter - the same console pattern as the Huddle orchestrator, staged here as a reusable worked example
- **`firebug scan`** - network discovery from the command line, and the worked example for SsdpCore's client side
  - `firebug scan` - SSDP sweep (`ssdp:all`), results named from each device's description
  - `--target <st>` - specific search target (e.g. `urn:schemas-upnp-org:device:MediaRenderer:1`)
  - `--mdns <type[,type]>` - mDNS/DNS-SD sweep instead (e.g. `_airplay._tcp`)
  - `--duration <ms>`, `--raw` (skip description fetch), `--verbose` (full wire log via the `SsdpTrace` switch; quiet by default, hard errors always print)
  - Exit code 0 when something answered, 1 when nothing did - scriptable
- SsdpCore is now merged into `firebug.exe` (same ILRepack single-file pattern as Firebug.dll)

---

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

### Fixed
- Package license now matches the repository: `Apache-2.0` (the csproj had said `MIT` since the initial commit; the repo `LICENSE` has always been Apache-2.0)
- `AssemblyVersion` pinned at 0.5.0.0 - it is the net48 binding identity, so patch bumps no longer break compiled consumers (`Firebug.csproj` gets the same pin)
- Description XML decoding honors the XML declaration's `encoding` attribute (`XDocument.Load(Stream)`) - ISO-8859-1 vendor names no longer silently mojibake, and BOM-less UTF-16 documents now parse; `Content-Type` remains untrusted and DTDs remain prohibited
- IPv4-mapped IPv6 LOCATIONs (`http://[::ffff:10.0.0.5]/`) unwrap to their IPv4 form - both for the LAN gate (was refused) and for the connection itself (net48's HTTP stack cannot reach a v4-mapped literal; measured)
- `SsdpDevice.FetchDescriptionAsync` uses the shared `DefaultTimeoutMs` (a divergent hardcoded 1500 remained) and now forwards a log callback, so LAN-gate refusals on the convenience path are diagnosable
- Disposing a scanner mid-scan no longer races an in-flight linked token source (`Cancel` without `Dispose`, applied to both scanners)
- NuGet metadata completed: XML docs shipped with the package (`GenerateDocumentationFile`), `RepositoryUrl`, `PackageProjectUrl`, `PackageReadmeFile`

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
