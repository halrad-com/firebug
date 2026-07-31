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
- Publish the public mirror; stabilize the surface before advertising it widely.
- NuGet package (`Halrad.Firebug`) once the API is settled.
- CLI verbs the design spec promises but doesn't yet implement:
  `firebug test` (TCP-connect probe to a host:port) and `firebug diagnose`
  (check + explain *why* a port is blocked); `list` is a nice-to-have. Until then
  the spec marks them "planned".

## Later — cross-plugin normalization (post-adoption)
The end state is that HALRAD's Windows plugins stop hand-rolling firewall / URL-ACL /
port logic and converge on Firebug:

- Retire bespoke per-project helpers — e.g. the MusicBee Chromecast plugin's
  `MBCCRules` elevated helper — in favor of `firebug.exe` / the library.
- One tried-and-true implementation shared across MBXHub, MBRC, MusicBee Chromecast,
  and future plugins, so a fix in one place benefits all.

**Deliberately deferred.** Do not start migrating consumers until Firebug has real
adoption and the API has proven stable (ideally ≥1 external consumer). Normalizing
too early locks in an interface before it's earned. Revisit as a planning item then;
this is a note-to-self, not scheduled work.
