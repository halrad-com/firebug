# SsdpCore

Shared SSDP (Simple Service Discovery Protocol) library for UPnP device discovery and advertising.

## Overview

SsdpCore provides two complementary capabilities:

| Class | Role | Use Case |
|-------|------|----------|
| `SsdpClient` | Discovery (M-SEARCH) | Find devices on the network |
| `SsdpServer` | Advertising (NOTIFY) | Announce your service to the network |

## Usage

### Client: Finding Devices

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

## Classes

### SsdpConstants

Protocol constants for SSDP:

```csharp
SsdpConstants.MulticastAddress   // "239.255.255.250"
SsdpConstants.Port               // 1900
SsdpConstants.DefaultMaxAge      // 1800 (30 minutes)
SsdpConstants.SearchTargetAll    // "ssdp:all"
SsdpConstants.SearchTargetAvTransport  // "urn:schemas-upnp-org:service:AVTransport:1"
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

    // Populated after fetching device description XML
    public string FriendlyName { get; set; }
    public string Manufacturer { get; set; }
    public string ModelName { get; set; }
    public string PresentationUrl { get; set; }

    public bool IsExpired { get; }
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

### MBXHub (Future)

MBXHub will use `SsdpServer` to advertise itself:

```csharp
var server = new SsdpServer(
    uuid: $"mbxhub-{machineGuid}-{port}",
    deviceType: "urn:halrad-com:device:MBXHub:1",
    getLocationUrl: ip => $"http://{ip}:{port}/device.xml",
    serverString: SsdpMessage.BuildServerString("MBXHub", version)
);
server.Start();
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
