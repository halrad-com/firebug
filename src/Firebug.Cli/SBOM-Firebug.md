# Software Bill of Materials (SBOM)

**Product:** firebug.exe — Windows Firewall, port and network-discovery utility
**Version:** 0.4.10.0
**Generated:** 2026-08-13
**Supplier:** Halrad LLC

---

## Summary

| Category | Count |
|----------|-------|
| Project Assemblies | 3 |
| NuGet Packages (Build-only) | 1 |
| Third-party Runtime Dependencies | 0 |

---

## Project Structure

firebug.exe is a single self-contained executable: the CLI plus two libraries
merged via ILRepack at build time.

| Assembly | Version | Type | Description |
|----------|---------|------|-------------|
| **firebug.exe** | 0.4.10.0 | Executable | Final merged utility |
| Firebug.Cli | 0.4.10.0 | Application | Verb dispatch, self-elevation, interactive prompt (line editor, Tab completion), completion emitter |
| Firebug | 0.5.0.1 | Library | Windows Firewall rules, URL ACL management, free-port selection (PortPicker) |
| SsdpCore | 0.5.0.2 | Library | LAN discovery: SSDP (UPnP), mDNS/DNS-SD, WS-Discovery; UPnP description fetch (LAN-scoped) |

### Project References

```
firebug.exe (merged output)
├── Firebug.Cli
│   ├── Firebug
│   └── SsdpCore
├── Firebug
│   └── (no dependencies beyond the BCL)
└── SsdpCore
    └── (no dependencies beyond the BCL)
```

---

## NuGet Package Dependencies

### Build-only Dependencies

| Package | Version | License | Description |
|---------|---------|---------|-------------|
| [ILRepack.Lib.MSBuild.Task](https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task) | 2.0.34.2 | MIT | MSBuild task to merge assemblies into the single EXE |

**Note:** ILRepack is a build tool only. It is not distributed with the utility.

### Runtime Dependencies

None. firebug.exe has no third-party runtime dependencies — everything below
is the .NET Framework BCL.

---

## .NET Framework Assemblies

Target framework: **.NET Framework 4.8**

| Assembly / Area | Purpose |
|----------|---------|
| System (Sockets) | `TcpListener` port probing (PortPicker); `UdpClient` multicast for SSDP/mDNS (SsdpCore) |
| System.Security.Principal | UAC elevation detection |
| System.Diagnostics.Process | Self-elevation via `runas` |
| System.Net.Http | SsdpCore's ONE HTTP use: fetching a discovered device's UPnP description XML (LAN-scoped, redirect-validated, 256 KB cap) |
| System.Xml.Linq | Parsing the UPnP description XML (`XDocument`; DTDs prohibited) |

---

## First-party Components

| Component | Files | License |
|-----------|-------|---------|
| Firebug.Cli | `src/Firebug.Cli/*` | Apache-2.0 |
| Firebug | `src/Firebug/*` | Apache-2.0 |
| SsdpCore | `src/SsdpCore/*` | Apache-2.0 |

All first-party code is licensed under the Apache License 2.0 (see the
repository `LICENSE`); an earlier revision of this document predated the
open-source release and listed these components as proprietary.

---

## License Summary

| License | Components |
|---------|------------|
| **Apache-2.0** | Firebug.Cli, Firebug, SsdpCore |
| **MIT** | ILRepack.Lib.MSBuild.Task (build-only) |
| **Microsoft EULA** | .NET Framework 4.8 |

---

## Dependency Graph

```
firebug.exe
├── Firebug.Cli (Apache-2.0)
├── Firebug (Apache-2.0)
├── SsdpCore (Apache-2.0)
└── .NET Framework 4.8 (Microsoft)
    ├── System / System.Net.Sockets
    ├── System.Security.Principal
    ├── System.Diagnostics.Process
    ├── System.Net.Http
    └── System.Xml.Linq
```

---

## Features

- Add/remove Windows Firewall rules (TCP/UDP), single and multi-port
- Add/remove HTTP URL ACL reservations
- Check firewall and URL ACL status
- Free-port selection: `pick` (parseable `PORT:` output), `reserve` (rule + ACL in one step, `--pick` combined flow)
- Network discovery: `scan` — SSDP (UPnP MediaRenderer or any search target) and mDNS/DNS-SD sweeps, results named from device descriptions
- Interactive prompt (bare invocation in a terminal): Tab completion of verbs and flags, grammar hints, history
- Shell tab-completion emitter: `completion powershell`
- Self-elevation via UAC only when needed; validation precedes elevation

---

## Vulnerability Tracking

For security vulnerability information:

- **.NET Framework:** Check https://msrc.microsoft.com/update-guide

---

## Contact

For licensing questions or SBOM inquiries:

**Email:** mbxhub@halrad.com
**Web:** https://halrad.com/

---

*Last updated: 2026-08-13*
