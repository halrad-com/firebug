using System.Net;
using System.Net.Sockets;
using Firebug;

namespace Firebug.Tests;

// Fast, no-elevation regression guards for the pure logic. The netsh-invoking
// methods (AddUrlAcl, AddTcpInboundRule, ...) have real side effects and need
// admin, so they are exercised by firebug.exe as a manual driver, not here.

public class BuildUrlAclArgsTests
{
    [Fact]
    public void AccountName_UsesUserParam()
    {
        Assert.Equal(
            "http add urlacl url=http://+:8000/ user=Everyone",
            FirebugManager.BuildUrlAclArgs(8000, "Everyone"));
    }

    [Fact]
    public void Sid_UsesSddl_NotUserParam()
    {
        // The 133e069 fix: a bare SID must go through sddl=, never user=.
        var args = FirebugManager.BuildUrlAclArgs(8000, "S-1-5-11");
        Assert.Contains("sddl=D:(A;;GX;;;S-1-5-11)", args);
        Assert.DoesNotContain("user=", args);
    }

    [Fact]
    public void NullUser_DefaultsToEveryone()
    {
        Assert.Contains("user=Everyone", FirebugManager.BuildUrlAclArgs(8000, null!));
    }

    [Fact]
    public void PortIsInterpolated()
    {
        Assert.Contains("http://+:12345/", FirebugManager.BuildUrlAclArgs(12345, "Everyone"));
    }
}

public class GenerateScriptTests
{
    [Fact]
    public void ContainsAppNamePortAndBothCommands()
    {
        var s = new FirebugManager().GenerateScript("MyApp", 8000);
        Assert.Contains("MyApp", s);
        Assert.Contains("8000", s);
        Assert.Contains("add urlacl", s);
        Assert.Contains("advfirewall firewall add rule", s);
    }
}

public class PortPickerTests
{
    // Bind a listener on an OS-assigned port; that port is now busy for the test's life.
    private static TcpListener Occupy(out int port)
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        return l;
    }

    [Fact]
    public void IsFree_False_WhenPortBound()
    {
        var l = Occupy(out int port);
        try { Assert.False(PortPicker.IsFree(port)); }
        finally { l.Stop(); }
    }

    [Fact]
    public void IsFree_True_AfterReleased()
    {
        var l = Occupy(out int port);
        l.Stop();
        Assert.True(PortPicker.IsFree(port));
    }

    [Fact]
    public void Pick_SkipsBusyPreferred_ReturnsHigherFreePort()
    {
        var l = Occupy(out int busy);
        try
        {
            int got = PortPicker.Pick(busy, tries: 50);
            Assert.True(got > busy);
            Assert.True(PortPicker.IsFree(got));
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void Resolve_ReusesSavedPort_WhenFree()
    {
        var l = Occupy(out int port);
        l.Stop();
        Assert.Equal(port, PortPicker.Resolve(port, preferred: 40000));
    }

    [Fact]
    public void Resolve_PicksNew_WhenSavedBusy()
    {
        var l = Occupy(out int busy);
        try
        {
            int got = PortPicker.Resolve(busy, preferred: busy + 1);
            Assert.NotEqual(busy, got);
            Assert.True(PortPicker.IsFree(got));
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void PickPair_ReturnsPortWhereBothItAndNeighborAreFree()
    {
        int low = PortPicker.PickPair(41000);
        Assert.True(PortPicker.IsFree(low));
        Assert.True(PortPicker.IsFree(low + 1));
    }
}

// Rule health (review 2026-08-22, R3/R4). HasRule was output.Contains(name) —
// presence only — so a disabled rule, a Block rule, an outbound rule, or a rule
// left behind on an old port all reported PRESENT, and callers whose repair
// contract is "add only if absent" then never fixed them. These pin the decision
// logic; the COM enumeration that feeds it is exercised by firebug.exe by hand.

public class RuleHealthTests
{
    private static FirebugManager.FirewallRuleView Rule(
        string name = "MBXHub", bool enabled = true, bool inbound = true,
        bool allow = true, int protocol = 6, string ports = "8080")
        => new FirebugManager.FirewallRuleView(name, enabled, inbound, allow, protocol, ports);

    [Fact]
    public void HealthyRule_Allows()
    {
        Assert.True(FirebugManager.RulesAllow(new[] { Rule() }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void DisabledRule_DoesNotAllow()
    {
        // R3: this is the case that reported "present" and was then left untouched.
        Assert.False(FirebugManager.RulesAllow(new[] { Rule(enabled: false) }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void OutboundRule_DoesNotAllowInbound()
    {
        Assert.False(FirebugManager.RulesAllow(new[] { Rule(inbound: false) }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void BlockRule_Wins_EvenAlongsideAnAllow()
    {
        // Windows resolves block over allow regardless of order, so the presence of a
        // healthy allow rule must NOT rescue the verdict.
        var rules = new[] { Rule(allow: true), Rule(allow: false) };
        Assert.False(FirebugManager.RulesAllow(rules, "MBXHub", 6, "8080"));
        Assert.False(FirebugManager.RulesAllow(rules.Reverse().ToArray(), "MBXHub", 6, "8080"));
    }

    [Fact]
    public void StaleRuleOnTheOldPort_DoesNotAllowTheNewOne()
    {
        // R4: the rule survives a port change, so name-only matching says healthy
        // while the port that matters is shut.
        Assert.False(FirebugManager.RulesAllow(new[] { Rule(ports: "8080") }, "MBXHub", 6, "9090"));
    }

    [Fact]
    public void OneEnabledSibling_IsEnough()
    {
        // Several rules may share a name (one per profile is routine). A disabled
        // sibling must not veto a healthy one — only a BLOCK does that.
        var rules = new[] { Rule(enabled: false), Rule(enabled: true) };
        Assert.True(FirebugManager.RulesAllow(rules, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void WrongProtocol_DoesNotAllow()
    {
        Assert.False(FirebugManager.RulesAllow(new[] { Rule(protocol: 17) }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void AnyProtocolRule_SatisfiesASpecificAsk()
    {
        Assert.True(FirebugManager.RulesAllow(new[] { Rule(protocol: 256) }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void NameIsMatchedCaseInsensitively_ButOnlyExactly()
    {
        Assert.True(FirebugManager.RulesAllow(new[] { Rule(name: "mbxhub") }, "MBXHub", 6, "8080"));
        // "MBXHub-Shell" is a DIFFERENT rule covering a different port set — a
        // substring match here would recreate the false-green in MBXHub's repair.
        Assert.False(FirebugManager.RulesAllow(new[] { Rule(name: "MBXHub-Shell") }, "MBXHub", 6, "8080"));
    }

    [Fact]
    public void EveryWantedPortMustBeCovered()
    {
        Assert.True(FirebugManager.PortsCovered("8080,8081", "8080,8081"));
        Assert.False(FirebugManager.PortsCovered("8080", "8080,8081"));
    }

    [Fact]
    public void RangesAndWildcardAreUnderstood()
    {
        Assert.True(FirebugManager.PortsCovered("5000-5010", "5005"));
        Assert.False(FirebugManager.PortsCovered("5000-5010", "5011"));
        Assert.True(FirebugManager.PortsCovered("*", "8080"));
    }

    [Fact]
    public void UnreadablePortToken_CoversNothing()
    {
        // netsh keywords like "RPC"/"Teredo". Cannot be shown to include the port,
        // and claiming healthy on a rule we cannot read is the whole defect.
        Assert.False(FirebugManager.PortsCovered("RPC", "8080"));
    }

    [Fact]
    public void NoWantedPorts_AsksOnlyAboutExistence()
    {
        Assert.True(FirebugManager.PortsCovered("8080", null));
        Assert.True(FirebugManager.RulesAllow(new[] { Rule(ports: "9999") }, "MBXHub", 6, null));
    }
}
