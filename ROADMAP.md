# Firebug Roadmap

Firebug consolidates the "stand up a LAN HTTP listener on Windows" chores — pick a
free port, then authorize it (URL ACL + firewall) — into one small, BCL-only,
Apache-2.0 library + CLI. This is the direction, not a promise of dates.

## Recently landed

- `PortPicker` — free-port selection (`IsFree` / `Pick` / `PickPair` / `Resolve`)
  to complement the firewall/URL-ACL setup in `FirebugManager`.
- Apache-2.0 `LICENSE`; repo prepared for a public mirror at `halrad-com/firebug`.
- Unit tests for the pure logic (`BuildUrlAclArgs`, `GenerateScript`, `PortPicker`);
  `firebug.exe` remains the manual/elevated end-to-end driver.
- `release-tools/` — private→public sync (full mirror + leak gate), never auto-pushes.

## Near term

- NuGet package (`Halrad.Firebug`) once the API is settled.
- CLI verbs the design spec promises but doesn't yet implement:
  `firebug test` (TCP-connect probe to a host:port) and `firebug diagnose`
  (check + explain *why* a port is blocked); `list` is a nice-to-have. Until then
  the spec marks them "planned".
- Diagnostics
- Port conflict remediation help
