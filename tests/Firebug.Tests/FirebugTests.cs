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
