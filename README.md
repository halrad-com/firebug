# Fireants

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

SSDP (Simple Service Discovery Protocol) library for UPnP device discovery and advertising.

**Library:** `SsdpCore.dll` - Reference in your .NET projects

| Class | Role | Use Case |
|-------|------|----------|
| `SsdpClient` | Discovery (M-SEARCH) | Find devices on the network |
| `SsdpServer` | Advertising (NOTIFY) | Announce your service to the network |

```csharp
// Client: Find devices
var client = new SsdpClient();
client.DeviceFound += (s, device) => Console.WriteLine(device.Location);
client.StartListening();
client.SendSearch("ssdp:all");

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

---

## Consumers

- **MBXHub** - MusicBee REST API plugin (Firebug, SsdpCore)
- **PhantomBee** - MusicBee UPnP streaming plugin (SsdpCore)
- **MBXRemote/tntctl** - MusicBee remote control (Firebug)
- **MusicBee Chromecast** - Chromecast integration
- Future projects

## Why "Fireants"?

Fire + ants = small but mighty. Also: firewall + antidotes.

The Firebug component does the actual work.

## License

[Apache License 2.0](LICENSE) © 2026 Halrad LLC.
