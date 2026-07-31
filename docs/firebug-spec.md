# Firebug

**Windows Firewall Configuration Done for sharing**

Repo: https://github.com/halrad-com/firebug

> **Status:** This is the original design spec and reflects the intended vision.
> Some commands and APIs shown below are **planned, not yet implemented**. The
> authoritative list of what ships today is in the [README](../README.md).

## Vision

Every Windows app that needs network access fights the same battle: firewall configuration. Users get blocked, developers write the same netsh wrapper code, IT admins get frustrated.

Firebug ends this. One tool that makes Windows Firewall configuration trivial for developers and users alike.

## The Problem

1. **For Users**: "Why can't my app connect?" - buried settings, confusing UI, no diagnostics
2. **For Developers**: Every app reinvents firewall management, poorly
3. **For IT**: No visibility into what apps are requesting, manual rule management

## The Solution

### Firebug CLI

```bash
# --- Available now ---

# Add a firewall rule (optionally reserve the URL ACL too)
firebug add --name "My App" --port 8080 --urlacl

# Check whether the rule / reservation exists
firebug check --name "My App" --port 8080

# Remove the rule + reservation
firebug remove --name "My App" --port 8080

# Firewall + elevation status
firebug status

# Open the Windows Firewall UI
firebug open

# (add/remove self-elevate via UAC when they need admin — there is no separate 'elevate' verb.)

# --- Planned (not yet implemented) ---

# Diagnose why a port is blocked
firebug diagnose --port 8080
# e.g. Port 8080: BLOCKED by Windows Firewall (no inbound rule)

# List rules for an app
firebug list --name "My App"

# Test connectivity (attempt an actual connection)
firebug test --host 192.0.2.10 --port 8080
```

### Firebug Library

```csharp
// NuGet: Halrad.Firebug

var fb = new FirebugManager();

// Check status
bool enabled = fb.IsFirewallEnabled();
bool hasRule = fb.HasRule("My App", 8080, Protocol.Tcp, Direction.Inbound);

// Add rules (throws if not elevated)
fb.AddRule(new FirewallRule {
    Name = "My App",
    Port = 8080,
    Protocol = Protocol.Tcp,
    Direction = Direction.Inbound
});

// Diagnose
var result = fb.Diagnose(8080);
// result.IsOpen, result.BlockedBy, result.Recommendation

// Batch operations
fb.AddRules(new[] {
    FirewallRule.TcpInbound("My App - REST", 8080),
    FirewallRule.TcpInbound("My App - WebSocket", 8081),
    FirewallRule.UdpInbound("My App - Discovery", 5900)
});

// Elevation helper
if (!fb.IsElevated) {
    fb.RestartElevated(); // Relaunches process with UAC
}
```

### Firebug GUI (stretch goal)

Simple wizard for end users:

1. "What app?" (browse or detect running apps)
2. "What ports?" (auto-detect from app config or manual entry)
3. "Configure" (one-click setup with UAC prompt)
4. "Test" (verify connectivity)

## Technical Approach

### Core Implementation

- Windows Firewall API (`INetFwPolicy2` COM interface) - proper API, not netsh scraping
- Fallback to `netsh advfirewall` for edge cases
- No external dependencies

### Elevation Handling

- Detect if running elevated
- Provide helper to relaunch with UAC
- Support "run elevated command only" pattern (elevate just for the firewall change)

### Diagnostics

- Check Windows Firewall status (enabled/disabled per profile)
- Check for conflicting rules
- Check for third-party firewalls (detect common ones)
- Network connectivity testing (TCP connect, UDP broadcast)
- Clear, actionable error messages

## Consumers

### Immediate

- **MBXHub** - REST 8080, WebSocket 8081
- **TMRemote/tntctl** - MBRC 3000, Discovery UDP 5900
- **Any future MBX project**

### Potential

- Open source release
- Other developers with same problem
- IT admin tool

## Prior Art (to port/learn from)

- `tntctl/Services/FirewallManager.cs` - working implementation, netsh-based
- `tntctl/UX/SettingsView.Firewall.cs` - UI patterns, status indicators

## Non-Goals (for now)

- Third-party firewall management (Norton, McAfee, etc.) - detect only, don't configure
- Linux/macOS - Windows only
- Enterprise/GPO scenarios - focus on home/small network
- Outbound rules - focus on inbound (apps accepting connections)

## Milestones

### v0.1 - Core Library

- [ ] `FirebugManager` class
- [ ] Add/remove/check rules
- [ ] Elevation detection and helper
- [ ] Basic diagnostics

### v0.2 - CLI Tool

- [ ] `firebug` command-line interface
- [ ] All core operations exposed
- [ ] Human-readable output
- [ ] JSON output for scripting

### v0.3 - Diagnostics

- [ ] Comprehensive connectivity testing
- [ ] Third-party firewall detection
- [ ] Actionable recommendations

### v1.0 - Production Ready

- [ ] NuGet package
- [ ] Standalone CLI distribution
- [ ] Documentation
- [ ] MBXHub integration proven

### Future

- [ ] GUI wizard
- [ ] Installer/uninstaller hooks
- [ ] PowerShell module

## Name

**Firebug** - short, memorable, obvious what it does.

(Yes, there was a Firefox extension called Firebug. It's dead. We're not a browser tool. Name's free.)

---

*Parked for later. When the time comes, this is the vision.*
