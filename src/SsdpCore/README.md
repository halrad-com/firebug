# SsdpCore

Shared SSDP (Simple Service Discovery Protocol) library for UPnP device discovery and advertising.

## Overview

SsdpCore provides complementary discovery capabilities:

| Class | Role | Use Case |
|-------|------|----------|
| `SsdpScanner` | One-shot discovery sweep | "What's on the network right now?" — scan, get a named device list |
| `SsdpClient` | Continuous discovery (M-SEARCH) | Long-lived listening with an event per device |
| `SsdpServer` | Advertising (NOTIFY) | Announce your service to the network |
| `SsdpDescription` | Device description fetch | Name a discovered device from its UPnP description XML |

The library also ships `MdnsScanner` (mDNS/DNS-SD sweep, same call shape as `SsdpScanner`) and WS-Discovery support (`WsdServer`).

## Usage

### Scanner: One-Shot Sweep (start here)

```csharp
using SsdpCore;

using (var scanner = new SsdpScanner(log: (level, msg) => Console.WriteLine($"[{level}] {msg}")))
{
    // Returns after ~3s with deduplicated, NAMED devices — descriptions are
    // fetched automatically (pass fetchDescriptions: false for a raw sweep).
    var devices = await scanner.ScanAsync(SsdpConstants.SearchTargetMediaRenderer);
    foreach (var d in devices)
        Console.WriteLine($"{d.FriendlyName} ({d.ModelName}) at {d.Address} — {d.Location}");
}
```

### Client: Finding Devices (continuous)

```csharp
using SsdpCore;

var client = new SsdpClient(log: (level, msg) => Console.WriteLine($"[{level}] {msg}"));
client.DeviceFound += (sender, device) =>
{
    Console.WriteLine($"Found: {device.Location}");
    // device.Id, device.DeviceType, device.Address, etc.
};

client.StartListening();
client.SendSearch(SsdpConstants.SearchTargetAvTransport);  // Or "ssdp:all"

// Later...
client.StopListening();
client.Dispose();
```

### Server: Advertising a Service

```csharp
using SsdpCore;

var server = new SsdpServer(
    uuid: "my-device-" + Environment.MachineName,
    deviceType: "urn:halrad-com:device:MBXHub:1",
    getLocationUrl: localIp => $"http://{localIp}:8080/device.xml",
    serverString: SsdpMessage.BuildServerString("MBXHub", "1.0"),
    log: (level, msg) => Console.WriteLine($"[{level}] {msg}")
);

server.Start();   // Sends NOTIFY alive, starts listening for M-SEARCH
// Service runs...
server.Stop();    // Sends NOTIFY byebye
server.Dispose();
```

## Logging

Every class takes an optional `Action<TraceLevel, string>` sink. What reaches
it is governed by a library-wide switch — no filter logic needed in your callback:

```csharp
SsdpTrace.Switch.Level = TraceLevel.Warning;  // quiet: warnings + errors only
SsdpTrace.Switch.Level = TraceLevel.Off;      // silent — except hard errors
SsdpTrace.Switch.Level = TraceLevel.Verbose;  // everything (the default)
```

Also configurable without code via `app.config` (TraceSwitch name `"SsdpCore"`).
**Hard errors always log**: `TraceLevel.Error` bypasses the switch — the only
way to silence errors is to pass no sink at all. The default is Verbose, so
consumers that predate the switch see the exact stream they always had.

## Classes

### SsdpConstants

Protocol constants for SSDP:

```csharp
SsdpConstants.MulticastAddress   // "239.255.255.250"
SsdpConstants.Port               // 1900
SsdpConstants.DefaultMaxAge      // 1800 (30 minutes)
SsdpConstants.SearchTargetAll    // "ssdp:all"
SsdpConstants.SearchTargetRootDevice   // "upnp:rootdevice"
SsdpConstants.SearchTargetAvTransport  // "urn:schemas-upnp-org:service:AVTransport:1"
SsdpConstants.SearchTargetMediaRenderer // "urn:schemas-upnp-org:device:MediaRenderer:1"
```

### SsdpMessage

Static utilities for parsing and building SSDP messages:

```csharp
// Parsing
string location = SsdpMessage.ParseHeader(response, "LOCATION");
var headers = SsdpMessage.ParseHeaders(message);
bool isSearch = SsdpMessage.IsMSearch(message);

// Building
string search = SsdpMessage.BuildMSearch("ssdp:all", mx: 3);
string alive = SsdpMessage.BuildNotifyAlive(location, nt, usn, serverString);
string byebye = SsdpMessage.BuildNotifyByeBye(nt, usn);
string response = SsdpMessage.BuildSearchResponse(location, st, usn, serverString);
```

### SsdpDevice

Data structure for discovered devices:

```csharp
public class SsdpDevice
{
    public string Id { get; set; }           // USN
    public string DeviceType { get; set; }   // ST/NT
    public string Location { get; set; }     // Device description URL
    public IPAddress Address { get; set; }
    public DateTime LastSeen { get; set; }
    public int MaxAge { get; set; }
    public string ServerString { get; set; } // SERVER header

    // Populated by SsdpDescription (description XML fetch)
    public string FriendlyName { get; set; }
    public string Manufacturer { get; set; }
    public string ModelName { get; set; }
    public string ModelDescription { get; set; }
    public string PresentationUrl { get; set; }
    public bool DescriptionFetched { get; set; }

    public bool IsExpired { get; }

    // Convenience over SsdpDescription.FetchAsync
    public Task<bool> FetchDescriptionAsync(int timeoutMs = 1500, CancellationToken ct = default);
}
```

### SsdpScanner

One-shot discovery sweep — send M-SEARCH, listen for a window, return a
deduplicated list. Sends each search twice (UDP reliability), filters
unsolicited NOTIFY traffic to the requested targets, and by default fetches
each device's description so results come back named:

```csharp
public class SsdpScanner : IDisposable
{
    // Fires once per unique device at DISCOVERY time (before description fetch)
    public event EventHandler<SsdpDevice> DeviceFound;

    public Task<List<SsdpDevice>> ScanAsync(
        string searchTarget,               // or IEnumerable<string>
        int scanDurationMs = 3000,
        bool fetchDescriptions = true,
        CancellationToken cancellationToken = default);
}
```

`"ssdp:all"` as a target matches every response. One scan runs at a time per
instance; a concurrent call returns an empty list with a warning.

### SsdpDescription

Fetches the UPnP description XML behind a device's `LOCATION` URL and fills
the name fields. Device-agnostic: reads only the standard root-`<device>`
elements, matched by local name. Never throws — an unreachable or malformed
description leaves the device unnamed and returns `false`.

Hardened for hostile LANs: `LOCATION` is device-controlled, so fetches are
refused unless the host is an IP literal in private / link-local / unique-local
space (DNS names refused outright), and response bodies are capped at 256 KB.
Redirects are followed manually — max 3 hops, every hop re-validated through
the same LAN gate, one deadline for the whole chain — so a device can
http→https itself but a 302 can never leave the LAN. Content is decoded from
raw bytes (BOM-sniffed UTF-8) because real devices send `Content-Type`
charsets .NET rejects:

```csharp
public static class SsdpDescription
{
    public static Task<bool> FetchAsync(
        SsdpDevice device,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default,
        Action<TraceLevel, string> log = null);
}
```

### SsdpClient

Discovers devices via M-SEARCH:

```csharp
public class SsdpClient : IDisposable
{
    public event EventHandler<SsdpDevice> DeviceFound;

    public void StartListening();
    public void StopListening();
    public void SendSearch(string searchTarget, int mx = 3);

    // Override for custom filtering
    protected virtual bool ShouldIncludeDevice(SsdpDevice device) => true;
}
```

### SsdpServer

Advertises a device via NOTIFY and responds to M-SEARCH:

```csharp
public class SsdpServer : IDisposable
{
    public int MaxAge { get; set; }  // Cache control (default: 1800s)
    public TimeSpan InitialBurstInterval { get; set; }  // First announcement delay

    public void Start();      // Begin advertising
    public void Stop();       // Send byebye and stop
    public void SendAlive();  // Manual refresh
}
```

## Integration

### PhantomBee

PhantomBee uses `SsdpClient` for Phantom device discovery:

```csharp
// PhantomDiscovery wraps SsdpClient with Phantom-specific filtering
var discovery = new PhantomDiscovery(log);
discovery.DeviceFound += (s, phantom) => { /* AVTransport device found */ };
discovery.StartDiscovery();
```

### MBXHub

MBXHub uses `SsdpServer` to advertise itself for peer discovery, and
`SsdpScanner` for its LAN device scan (`POST /devices/endpoints/scan?protocol=ssdp`,
which the WiiM charm uses to find LinkPlay streamers):

```csharp
// Advertise
var server = new SsdpServer(
    uuid: $"mbxhub-{machineGuid}-{port}",
    deviceType: "urn:halrad-com:device:MBXHub:1",
    getLocationUrl: ip => $"http://{ip}:{port}/device.xml",
    serverString: SsdpMessage.BuildServerString("MBXHub", version)
);
server.Start();

// Sweep for UPnP renderers, names included
var renderers = await scanner.ScanAsync(SsdpConstants.SearchTargetMediaRenderer);
```

## Build

SsdpCore is merged into consuming projects via ILRepack for single-file deployment. No separate DLL to distribute.

## Protocol Reference

SSDP is part of UPnP Device Architecture 1.1:

- **M-SEARCH**: Multicast query to find devices
- **NOTIFY ssdp:alive**: Device announcing presence
- **NOTIFY ssdp:byebye**: Device leaving network
- **HTTP 200 OK**: Unicast response to M-SEARCH

All messages use UDP multicast on `239.255.255.250:1900`.
