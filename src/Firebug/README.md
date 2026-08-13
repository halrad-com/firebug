# Firebug

Windows Firewall and URL ACL configuration, made programmatic — plus free-port
selection. The library behind the `firebug` CLI, usable on its own.

## What it does

- **FirebugManager** — add/remove/check Windows Firewall inbound rules (TCP/UDP,
  single or multi-port) and HTTP.SYS URL ACL reservations (the thing
  `HttpListener` apps need to bind without running elevated), detect elevation,
  open the firewall settings UI.
- **PortPicker** — choose a listener port that actually works: `IsFree(port)`
  probes bindability right now, `Pick(preferred)` walks up from a preferred
  port, `PickPair(preferred)` finds an adjacent free pair (server +
  side-channel), and `Resolve(savedPort, preferred)` honors a persisted port
  while it is still free. Persist the result so the URL ACL and firewall rule
  stay aligned across restarts.

The two are companions: PortPicker chooses the port, FirebugManager authorizes it.

## Quick start

```csharp
using Firebug;

var fb = new FirebugManager();
int port = PortPicker.Resolve(savedPort: settings.Port, preferred: 8000);

if (!fb.HasRule("My App"))
    fb.AddTcpInboundRule("My App", port, Console.WriteLine);   // needs elevation
if (!fb.HasUrlAcl(port))
    fb.AddUrlAcl(port, "Everyone", Console.WriteLine);         // needs elevation

settings.Port = port;   // persist — Resolve reuses it next launch
```

Multi-targets `net48;net8.0` (Windows only — this is Windows Firewall). BCL
only; no third-party dependencies.

## Prefer to shell out?

The same capabilities ship as the single-file `firebug.exe`
(`add` / `remove` / `check` / `pick` / `reserve` / `scan`, self-elevating via
UAC only when needed). See the repository README.

## License

Apache-2.0 — see the repository `LICENSE`.
