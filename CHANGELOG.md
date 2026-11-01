# Firebug Changelog

> **Version labels** use shipped assembly-file versions. Earlier drafts of this
> file - and some pre-relabel commit messages - referred to the two SsdpCore
> entries below as 1.1.0 / 1.0.0, and to the 2026-07-30 Firebug entry as 0.2.0
> (no 0.2.0 assembly ever existed; that work shipped as 0.5.0.1). The
> 2026-01-19 Firebug entry really did ship as 0.1.0.

## Firebug [Unreleased]

### Added
- **`PortOwner` - names the process behind an http.sys port.** The TCP table cannot:
  every `HttpListener` binds through the kernel-mode HTTP driver, so
  `GetExtendedTcpTable` / `netstat` report the owner as `System (PID 4)` - true, and
  useless, because "System" is not a program a user can be told to close. http.sys
  itself knows the answer, and `netsh http show servicestate view=requestq` reports it:
  request queues, their attached processes, and each registered URL prefix. `Parse` is
  pure over that text (testable against captured dumps, no elevation, no live listener),
  `ForPort` filters it, and the live overload shells netsh with the drain-before-wait
  discipline the ACL reads already use. Anchored on `HTTP://` and `ID:` because netsh's
  LABELS are localized on non-English Windows while its payloads are not
  - Three prefix forms, two of them traps: `+:8080` (one colon), `127.0.0.1:8081:127.0.0.1`
    (explicit IP, printed host:port:ip - "after the last colon" reads the port as 127),
    and `[::1]:28391` (an IPv6 literal is mostly colons; the bracket delimits the host)
  - A queue header appears twice - once at column 0 opening the queue, and again indented
    inside each URL group naming its owner - so only the column-0 one starts a new queue.
    A synthetic fixture without the nested line stayed green while the live run parsed
    every row to PID 0; the fixture now mirrors real output
  - LIVE state, not configuration: a process that is not running has registered nothing,
    so no rows means "not listening", never "not configured"
- **`HasAnyRuleForPorts` / `TryAnyRuleForPorts` - does any enabled inbound rule, whatever its name, let these ports in to the caller's program?** Rules scoped to a service (the built-in mDNS, SSDP and WSD rules) do not count, rules scoped to an application count only for the paths passed in, a block among the rules that apply still wins, and an unreadable rule set returns null from `TryAnyRuleForPorts` and false from `HasAnyRuleForPorts`
  - Rules scoped to a Store app package do not count either, and neither does a rule whose service is `*` ("services only" - scoped, not "any program")
  - `FindBlockingRule` / `BlockingRule` name the block rule that keeps a port shut, under the same scope rules, so a status surface can say which rule to look at; null when nothing blocks or the rule set cannot be read

### Fixed
- **`PortPicker.IsFree` tested only IPv4, so it called occupied ports free.** It bound
  `IPAddress.Any` - which is `0.0.0.0`, one address family - while the bind it exists to
  predict takes both: a caller reserving `http://+:{port}/`, or binding the IPv6 wildcard,
  is stopped by an occupant on *either* stack. A port held on `[::]` and free on `0.0.0.0`
  therefore reported free, and the caller's real bind then failed - the failure landing at
  bind time, far from the check that approved it. Windows sockets are v6-only by default,
  so a one-family occupant is an ordinary state rather than an exotic one. `IsFree` now
  tests both families, and treats *address family unavailable* as "not an occupancy" so a
  machine with IPv6 disabled does not report every port taken

## SsdpCore [Unreleased]

### Added
- **`MdnsScanner.ScanAsync` can key results by service instance name.** New overloads take an `MdnsScanKey`: `IpAddress` (the default, and what the existing overloads still do) keeps one service per IP, first in wins; `InstanceName` keeps every instance a device announces. A Google Cast speaker group is announced by its lead speaker, at the leader's IP, under its own instance name and port, so keying by IP dropped either the group or the leader. The Devialet scan relies on IP dedupe and is unchanged. The existing signatures are kept, so already-compiled consumers still bind; the dedupe decision is a pure helper pinned by tests

### Fixed
- **`MdnsScanner` asks for a multicast reply, not a unicast one.** The PTR query set the unicast-response bit. Google Cast receivers honor it and answer unicast to port 5353, and on Windows a unicast datagram to that port goes to the DNS Client service, which bound it first, never to the scanner's socket. Measured on one box with the scanner's own bind recipe: 0 of 4 Cast receivers with the bit, 4 of 4 without. Devialet and HTTP responders answered multicast regardless, which is why it never showed before a caller asked for `_googlecast._tcp`
- **`MdnsScanner` returns only the service types you asked for.** A PTR record was matched against the requested types, and then added to the results whether or not it matched (the computed `matchedType` was used to *label* the row, never to gate it). Because the scanner binds `0.0.0.0:5353` and joins the group on every interface, it hears every mDNS announcement on the machine for the whole scan window - printers, Cast targets, anything - and each of those PTRs became a "found service". Results are keyed by IP and first-in wins, so an unrequested row could also take the slot for a service the same device announced moments later
- **Type matching is anchored instead of a substring search.** The old test asked whether either name appeared *inside* the other, so `_tcp.local` satisfied a query for `_devialet._tcp.local`. Both sides are now normalised and compared whole, with a label-boundary suffix allowance so DNS-SD subtypes (`_printer._sub._http._tcp`) still match their base type
- **The A-record fallback no longer re-admits what the gate rejected.** It fired when the *results* list came out empty, which after the gate includes packets carrying many unmatched PTRs; it is now guarded on there being no PTR records at all, which is the case it was written for
- **`MdnsScanner` sends its query on every live adapter, not the one Windows picks.** The query went out once, on whichever adapter had the lowest multicast route metric; on a machine where that was a dead self-assigned (169.254.x) link, every scan found nothing while the devices answered direct requests. Each query now goes out once per live IPv4 adapter (up, not loopback or tunnel, not 169.254.x). One adapter failing does not stop the others; with no live adapter, or when every per-adapter send fails, it falls back to the old single send. The scan logs one line naming the adapters the query went out on

## Firebug.Cli [0.4.10.0] - 2026-08-13

### Added
- **`firebug completion powershell`** - shell tab-completion, generated at runtime from the CLI's own command catalog (one profile line: `firebug completion powershell | Out-String | Invoke-Expression`). Script is side-effect-free, never required for the tool to function, and safe in constrained sessions
- **`CommandCatalog`** - machine-readable catalog of every verb and flag (net48-safe plain shapes, per the cross-repo completion spec, so other tools can copy it). The completion emitter, the interactive completer and the parity self-tests all read this one table; bidirectional source-level tests pin it against the hand parsers so the two cannot drift
- **Interactive prompt upgrades** - Tab now completes the current verb's *flags* too (long and short forms), a dim grammar hint appears the moment you type `verb ` (Tab-immune - hints are guidance, not candidates), and the renderer is wrap-proof: long lines horizontally scroll inside a single row instead of wrapping and desyncing the caret, with flicker-free in-place repaints (`LineViewport`, ported from the Huddle console)
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

### Fixed (review wave, same release)
- **Security:** the elevated relaunch now quotes arguments per `CommandLineToArgvW` rules and `reserve` validates `--name`/`--protocol` before elevation - a crafted `--name` containing quotes could previously smuggle flags (e.g. `--pick`) into the Administrator child, defeating the concrete-port invariant. Pinned by round-trip tests through the real Windows parser
- The interactive prompt gained an exception boundary (a throwing verb no longer kills the session), prints `(exit N)` when an elevated child fails (its own console closes before you can read it), and restores console colors if Ctrl+C lands mid-paint
- `--protocol` values other than tcp/udp are now an error instead of silently becoming a TCP rule
- `reserve` announces the rule replacement it performs, and rejects garbage `--preferred` values instead of printing a bogus `PORT: 0`
- `PortPicker.IsFree` rejects port <= 0 (binding port 0 asks the OS for an ephemeral port and always succeeds - it lied for the "is THIS port free" question)
- REPL tokenizer preserves quoted empty arguments; `scan` saves/restores the process-global `SsdpTrace` level
- Test suite grown to 46: editor pins decoupled from the live catalog (fixed vocabulary), nine reference pins ported (history-cancels-cycle, cursor arithmetic, stash lifecycle, bounds), completer/catalog drift-guard pins, and `CommandLineToArgvW` round-trips of the hostile vectors
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

## Firebug [0.5.0.1] - 2026-07-30

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
