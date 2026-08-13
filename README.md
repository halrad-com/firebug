# Firebug

Shared .NET libraries for network and system configuration.

**Built to be shared.** These are the firewall / URL-ACL / free-port chores that
every Windows app that opens a LAN listener has to solve — extracted once, done
right, and published so nobody has to hand-roll them again. Licensed under the
[Apache License 2.0](LICENSE); small, BCL-only, no external dependencies — designed
to be **referenced as a library or vendored/federated** into your own tree, so every
consumer inherits the same tried-and-true approach without taking on a hard dependency.

## Projects

### Firebug

Everything you need to stand up a LAN HTTP listener on Windows: **pick a free port**
and **authorize it** (URL ACL + firewall). Consolidates the port/firewall/URL-ACL
patterns from several projects into one tried-and-true place.

**Library:** `Firebug.dll` - Reference in your .NET projects
**CLI:** `firebug.exe` - Standalone utility with admin manifest

### Usage

```cmd
# Add firewall rule and URL ACL
firebug add --name MBXHub --port 8080 --urlacl

# Add just firewall rule
firebug add --name "MusicBee Remote" --port 3000

# Check if rules exist
firebug check --name MBXHub --port 8080

# Remove rules
firebug remove --name MBXHub --port 8080

# Show firewall status
firebug status

# Open Windows Firewall settings
firebug open
```

### Library API

```csharp
var fb = new FirebugManager();

// Check status
bool enabled = fb.IsFirewallEnabled();
bool hasRule = fb.HasRule("MBXHub");
bool hasAcl = fb.HasUrlAcl(8080);
bool elevated = fb.IsElevated();

// Add rules (requires elevation)
fb.AddTcpInboundRule("MBXHub", 8080, Console.WriteLine);
fb.AddUdpInboundRule("MBXHub Discovery", 5900, Console.WriteLine);
fb.AddUrlAcl(8080, "Everyone", Console.WriteLine);

// Remove rules
fb.RemoveRule("MBXHub", Console.WriteLine);
fb.RemoveUrlAcl(8080, Console.WriteLine);

// Generate script for manual configuration
string script = fb.GenerateScript("MBXHub", 8080);

// Open Windows Firewall UI
fb.OpenFirewallSettings();
```

### Port selection

`PortPicker` chooses a free listener port so a fixed value never strands a user on a
busy port. Pair it with `FirebugManager`: PortPicker picks the port, FirebugManager
authorizes it. Persist the picked port so the URL ACL and firewall rule stay aligned
across restarts.

```csharp
// Reuse the saved port if still free, else ladder up from 8000.
int port = PortPicker.Resolve(settings.Port, preferred: 8000, Console.WriteLine);
settings.Port = port;              // persist

PortPicker.IsFree(9000);           // probe a specific port
PortPicker.Pick(8000);             // first free port at/above 8000
PortPicker.PickPair(8000);         // low port of first free adjacent pair (server + side-channel)
```

### Consuming it — bind the DLL, or vendor the source

Firebug is BCL-only, so take it whichever way suits your project.

**1. NuGet package** (once published):

```xml
<PackageReference Include="Halrad.Firebug" Version="0.5.*" />
```

**2. Bind the assembly** — reference the project or a dropped-in `Firebug.dll`:

```xml
<ProjectReference Include="path\to\src\Firebug\Firebug.csproj" />
<!-- or a compiled DLL: -->
<Reference Include="Firebug"><HintPath>libs\Firebug.dll</HintPath></Reference>
```

Firebug multi-targets `net48;net8.0`, so the same binary binds into .NET Framework
4.8 plugins (e.g. MusicBee) and modern .NET alike.

**3. Vendor the source (federate)** — zero dependency: copy just the file(s) you
need into your own tree and change the namespace. They pull in nothing beyond the BCL:

- `src/Firebug/PortPicker.cs` — free-port selection
- `src/Firebug/FirebugManager.cs` — firewall + URL ACL

This is the friction-free path when you don't want another package in your graph;
Apache-2.0 asks only that you keep the license/attribution notice.

**4. Shell out to the CLI** — no build-time reference at all:

```cmd
firebug add --name "My App" --port 8000 --urlacl
```

## Building

```cmd
cd src
dotnet build -c Release
```

Output:

- `src/Firebug/bin/Release/net48/Firebug.dll`
- `src/Firebug.Cli/bin/Release/net48/firebug.exe`
- `src/SsdpCore/bin/Release/net48/SsdpCore.dll`

---

### SsdpCore

Local-network discovery library — SSDP (UPnP), mDNS/DNS-SD, and WS-Discovery.
Find devices, name them, and advertise your own service.

**Library:** `SsdpCore.dll` - Reference in your .NET projects

| Class             | Role                        | Use Case                                                    |
| ----------------- | --------------------------- | ----------------------------------------------------------- |
| `SsdpScanner`     | One-shot SSDP sweep         | "What's on the network right now?" — scan, get named devices |
| `SsdpClient`      | Continuous SSDP (M-SEARCH)  | Long-lived listening with an event per device                |
| `SsdpServer`      | Advertising (NOTIFY)        | Announce your service to the network                         |
| `SsdpDescription` | Device description fetch    | Name a device from its UPnP description XML (LAN-scoped, hardened) |
| `MdnsScanner`     | One-shot mDNS/DNS-SD sweep  | Find Bonjour/Zeroconf services (AirPlay, Devialet, ...)      |
| `WsdServer`       | WS-Discovery responder      | Appear in Windows' Network view                              |
| `SsdpTrace`       | Logging switch              | Dial library log output up or down (hard errors always log)  |

```csharp
// One-shot sweep: deduplicated devices, named from their descriptions
using (var scanner = new SsdpScanner())
{
    var devices = await scanner.ScanAsync(SsdpConstants.SearchTargetMediaRenderer);
    foreach (var d in devices)
        Console.WriteLine($"{d.FriendlyName} ({d.ModelName}) at {d.Address}");
}

// Server: Advertise a service
var server = new SsdpServer(
    uuid: "my-device",
    deviceType: "urn:halrad-com:device:MBXHub:1",
    getLocationUrl: ip => $"http://{ip}:8080/device.xml",
    serverString: SsdpMessage.BuildServerString("MBXHub", "1.0")
);
server.Start();
```

See `src/SsdpCore/README.md` for full API documentation.

See: [DynaPort — a well-behaved listener port](https://mbxhub.com/downloads/examples/dynaport/)

---

## Consumers

- **[MBXHub](https://mbxhub.com/)** - MusicBee REST API plugin (Firebug, SsdpCore)
- **MBXRemote/tntctl** - MusicBee remote control (Firebug)
- ?yours goes here?

## Why "Firebug"?

Firewall + the little setup bug it swats. Small tool, one fiddly job, handled.

## License

[Apache License 2.0](LICENSE) © 2026 Halrad LLC.
