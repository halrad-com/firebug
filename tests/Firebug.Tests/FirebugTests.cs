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

    // A port can be busy on ONE address family only. Binding IPv6Any leaves the
    // IPv4 loopback/wildcard free (Windows sockets are v6-only by default), which
    // is exactly the state a v4-only probe answers "free" about — and then the
    // caller's wildcard bind fails, because http.sys/`+` takes both families.
    private static TcpListener OccupyIPv6Only(out int port)
    {
        var l = new TcpListener(IPAddress.IPv6Any, 0);
        l.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        return l;
    }

    [Fact]
    public void IsFree_False_WhenOnlyIPv6Bound()
    {
        if (!Socket.OSSupportsIPv6) return;   // v6-disabled box: nothing to assert
        var l = OccupyIPv6Only(out int port);
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

    // AnyRuleAllows: the same decision with no name filter. A Windows built-in rule
    // ("mDNS (UDP-In)") counts — the question is whether the port gets in, not whose
    // rule lets it.

    [Fact]
    public void AnyRuleAllows_matches_a_rule_of_any_name()
    {
        var rules = new[] { Rule(name: "mDNS (UDP-In)", protocol: 17, ports: "5353") };
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_block_wins_over_allow()
    {
        // A block under ANY name shuts the port, whatever the allow is called.
        var rules = new[]
        {
            Rule(name: "MBXHub", allow: true, protocol: 17, ports: "5353"),
            Rule(name: "Corp", allow: false, protocol: 17, ports: "5353"),
        };
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(rules.Reverse().ToArray(), 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_ignores_disabled_outbound_and_other_protocols()
    {
        var rules = new[]
        {
            Rule(name: "Disabled", enabled: false, protocol: 17, ports: "5353"),
            Rule(name: "Outbound", inbound: false, protocol: 17, ports: "5353"),
            Rule(name: "Tcp", protocol: 6, ports: "5353"),
        };
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_needs_every_port_covered()
    {
        // Per-rule coverage, as PortsCovered decides it: two rules each covering one
        // port do not add up to a rule that covers both.
        var split = new[]
        {
            Rule(name: "SSDP", protocol: 17, ports: "1900"),
            Rule(name: "WSD", protocol: 17, ports: "3702"),
        };
        Assert.False(FirebugManager.AnyRuleAllows(split, 17, "1900,3702"));

        var single = new[] { Rule(name: "Both", protocol: 17, ports: "1900,3702") };
        Assert.True(FirebugManager.AnyRuleAllows(single, 17, "1900,3702"));
    }

    // Scope: Windows applies a rule only to the program or service it names. The
    // built-in "mDNS (UDP-In)" is scoped to the dnscache service, so it admits 5353 to
    // the DNS Client — never to another program's socket (review 2026-10-31, I-1).

    private const string HubExe = @"C:\Program Files\MBXHub\MBXHub.exe";

    private static FirebugManager.FirewallRuleView ScopedRule(
        string app = null, string service = null, bool allow = true,
        string name = "Scoped", int protocol = 17, string ports = "5353", string package = null)
        => new FirebugManager.FirewallRuleView(name, true, true, allow, protocol, ports, app, service, package);

    [Fact]
    public void AnyRuleAllows_ignores_a_service_scoped_rule()
    {
        var builtIn = new[] { ScopedRule(app: @"%SystemRoot%\system32\svchost.exe", service: "dnscache", name: "mDNS (UDP-In)") };
        Assert.False(FirebugManager.AnyRuleAllows(builtIn, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(builtIn, 17, "5353", new[] { HubExe }));

        // Never counts, block included: a service-scoped block does not shut another
        // program's port.
        var rules = new[] { Rule(protocol: 17, ports: "5353"), ScopedRule(service: "dnscache", allow: false) };
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_counts_an_application_scoped_rule_only_for_listed_paths()
    {
        var rules = new[] { ScopedRule(app: HubExe) };
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353", new[] { HubExe.ToUpperInvariant() }));
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353", new[] { @"C:\Program Files\MBXHub\sub\..\MBXHub.exe" }));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "5353", new[] { @"C:\Other\Other.exe" }));
    }

    [Fact]
    public void AnyRuleAllows_star_application_is_unscoped_but_star_service_is_scoped()
    {
        Assert.True(FirebugManager.AnyRuleAllows(new[] { ScopedRule(app: "*") }, 17, "5353"));
        // serviceName "*" means "services only" in the firewall API — it never admits
        // a program's socket (re-review N-1).
        Assert.False(FirebugManager.AnyRuleAllows(new[] { ScopedRule(service: "*") }, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(new[] { ScopedRule(app: "*", service: "*") }, 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_ignores_a_package_scoped_rule()
    {
        // A Store-app (AppContainer) rule has no application and no service, but it
        // admits that package only (re-review N-2).
        const string pkg = "S-1-15-2-1-2-3-4-5-6-7";
        Assert.False(FirebugManager.AnyRuleAllows(new[] { ScopedRule(package: pkg) }, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(new[] { ScopedRule(package: pkg) }, 17, "5353", new[] { HubExe }));

        var rules = new[] { Rule(protocol: 17, ports: "5353"), ScopedRule(package: pkg, allow: false) };
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353"));
    }

    [Fact]
    public void AnyRuleAllows_block_scoped_to_another_application_does_not_defeat_an_unscoped_allow()
    {
        var rules = new[] { Rule(protocol: 17, ports: "5353"), ScopedRule(app: @"C:\Other\Other.exe", allow: false) };
        Assert.True(FirebugManager.AnyRuleAllows(rules, 17, "5353", new[] { HubExe }));

        // ...but a block scoped to a listed application still wins.
        var ours = new[] { Rule(protocol: 17, ports: "5353"), ScopedRule(app: HubExe, allow: false) };
        Assert.False(FirebugManager.AnyRuleAllows(ours, 17, "5353", new[] { HubExe }));
    }

    [Fact]
    public void AnyRuleAllows_without_numeric_ports_is_false()
    {
        // Name-less AND port-less is "does any allow exist on the box" — true everywhere,
        // meaning nothing (review M-1). Guarded, not pinned.
        var rules = new[] { Rule(protocol: 17, ports: "*") };
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, null));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, ""));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "  "));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "mdns"));
        Assert.False(FirebugManager.AnyRuleAllows(rules, 17, "5353,mdns"));
    }

    [Fact]
    public void SixArgConstructor_yields_an_unscoped_rule()
    {
        var r = Rule();
        Assert.Null(r.ApplicationName);
        Assert.Null(r.ServiceName);
        Assert.Null(r.LocalAppPackageId);
    }

    [Fact]
    public void EightArgConstructor_leaves_the_package_null()
    {
        // Review M-5: the pre-INetFwRule3 overload carries app and service, never a package.
        var r = new FirebugManager.FirewallRuleView("Scoped", true, true, true, 17, "5353", HubExe, "dnscache");
        Assert.Null(r.LocalAppPackageId);
    }

    // Project: one COM rule reduced to a FirewallRuleView. The optional scope reads
    // (ApplicationName, serviceName, LocalAppPackageId) each sit in their own try, so a
    // rule whose one property throws is kept with that property null instead of being
    // dropped for every consumer — blocks included (review F-5).

    private static Func<string, object> ComRule(string? throwOn = null, int action = 0)
    {
        var values = new Dictionary<string, object>
        {
            ["Name"] = "Corp",
            ["Enabled"] = true,
            ["Direction"] = 1,     // NET_FW_RULE_DIR_IN
            ["Action"] = action,   // 0 = block, 1 = allow
            ["Protocol"] = 17,
            ["LocalPorts"] = "5353",
            ["ApplicationName"] = HubExe,
            ["serviceName"] = "dnscache",
            ["LocalAppPackageId"] = "S-1-15-2-1",
        };
        return property =>
        {
            if (property == throwOn) throw new InvalidOperationException("unreadable: " + property);
            return values[property];
        };
    }

    [Fact]
    public void Project_reads_every_field()
    {
        var view = FirebugManager.Project(ComRule(action: 1));
        Assert.NotNull(view);
        var r = view.Value;
        Assert.Equal("Corp", r.Name);
        Assert.True(r.Enabled);
        Assert.True(r.Inbound);
        Assert.True(r.Allow);
        Assert.Equal(17, r.Protocol);
        Assert.Equal("5353", r.LocalPorts);
        Assert.Equal(HubExe, r.ApplicationName);
        Assert.Equal("dnscache", r.ServiceName);
        Assert.Equal("S-1-15-2-1", r.LocalAppPackageId);
    }

    [Fact]
    public void Project_keeps_a_rule_whose_serviceName_read_throws()
    {
        var view = FirebugManager.Project(ComRule(throwOn: "serviceName"));
        Assert.NotNull(view);
        Assert.Equal("Corp", view.Value.Name);
        Assert.False(view.Value.Allow);
        Assert.Null(view.Value.ServiceName);
        Assert.Equal(HubExe, view.Value.ApplicationName);
        Assert.Equal("S-1-15-2-1", view.Value.LocalAppPackageId);
    }

    [Fact]
    public void Project_keeps_a_rule_whose_ApplicationName_read_throws()
    {
        var view = FirebugManager.Project(ComRule(throwOn: "ApplicationName"));
        Assert.NotNull(view);
        Assert.Null(view.Value.ApplicationName);
        Assert.Equal("dnscache", view.Value.ServiceName);
    }

    [Fact]
    public void Project_keeps_a_rule_whose_package_read_throws()
    {
        var view = FirebugManager.Project(ComRule(throwOn: "LocalAppPackageId"));
        Assert.NotNull(view);
        Assert.Null(view.Value.LocalAppPackageId);
        Assert.Equal("dnscache", view.Value.ServiceName);
    }

    [Fact]
    public void Project_marks_a_thrown_scope_read_unreadable()
    {
        Assert.False(FirebugManager.Project(ComRule())!.Value.ScopeUnreadable);
        Assert.True(FirebugManager.Project(ComRule(throwOn: "serviceName"))!.Value.ScopeUnreadable);
        Assert.True(FirebugManager.Project(ComRule(throwOn: "ApplicationName"))!.Value.ScopeUnreadable);
        Assert.True(FirebugManager.Project(ComRule(throwOn: "LocalAppPackageId"))!.Value.ScopeUnreadable);
    }

    // Unreadable scope (operator ruling on dc3d8e3, concern 3): such a rule may be a
    // service-scoped allow, so it never counts as an allow (no false green); it still
    // counts as a block, which errs safe.

    /// <summary>An unscoped COM rule (no app, service or package) whose one scope read throws.</summary>
    private static Func<string, object> UnscopedComRule(string throwOn, int action)
    {
        var inner = ComRule(throwOn, action);
        return property => property == throwOn ? inner(property)
            : property is "ApplicationName" or "serviceName" or "LocalAppPackageId" ? null! : inner(property);
    }

    [Fact]
    public void AnyRuleAllows_does_not_count_an_allow_whose_service_read_threw()
    {
        var allow = FirebugManager.Project(UnscopedComRule("serviceName", action: 1))!.Value;
        Assert.False(FirebugManager.AnyRuleAllows(new[] { allow }, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(new[] { allow }, 17, "5353", new[] { HubExe }));
    }

    [Fact]
    public void BlockingRule_counts_a_block_whose_service_read_threw()
    {
        var block = FirebugManager.Project(UnscopedComRule("serviceName", action: 0))!.Value;
        Assert.Equal("Corp", FirebugManager.BlockingRule(new[] { block }, 17, "5353"));
        Assert.False(FirebugManager.AnyRuleAllows(new[] { Rule(protocol: 17, ports: "5353"), block }, 17, "5353"));
    }

    [Fact]
    public void Project_drops_a_rule_whose_core_field_read_throws()
    {
        // Name, direction, action, protocol and ports decide what the rule does; a rule
        // missing one of them cannot be reasoned about, so it is still skipped.
        Assert.Null(FirebugManager.Project(ComRule(throwOn: "Action")));
        Assert.Null(FirebugManager.Project(ComRule(throwOn: "LocalPorts")));
    }

    // BlockingRule: the NAME of the block that keeps a port shut, under the same scope
    // rules as AnyRuleAllows, so a status surface can say which rule to look at.

    [Fact]
    public void BlockingRule_names_an_unscoped_block()
    {
        var rules = new[] { Rule(protocol: 17, ports: "5353"), Rule(name: "Corp block", allow: false, protocol: 17, ports: "5353") };
        Assert.Equal("Corp block", FirebugManager.BlockingRule(rules, 17, "5353"));
        Assert.Equal("Corp block", FirebugManager.BlockingRule(rules, 17, "5353", new[] { HubExe }));
    }

    [Fact]
    public void BlockingRule_ignores_a_block_scoped_to_another_program()
    {
        var rules = new[] { ScopedRule(app: @"C:\Other\Other.exe", allow: false, name: "Other block") };
        Assert.Null(FirebugManager.BlockingRule(rules, 17, "5353", new[] { HubExe }));
        Assert.Null(FirebugManager.BlockingRule(rules, 17, "5353"));
    }

    [Fact]
    public void BlockingRule_names_a_block_scoped_to_a_listed_program()
    {
        var rules = new[] { ScopedRule(app: HubExe, allow: false, name: "Hub block") };
        Assert.Equal("Hub block", FirebugManager.BlockingRule(rules, 17, "5353", new[] { HubExe }));
    }

    [Fact]
    public void BlockingRule_ignores_a_service_scoped_block()
    {
        var rules = new[] { ScopedRule(service: "dnscache", allow: false, name: "Service block") };
        Assert.Null(FirebugManager.BlockingRule(rules, 17, "5353", new[] { HubExe }));
    }

    [Fact]
    public void BlockingRule_with_no_rules_is_null()
    {
        Assert.Null(FirebugManager.BlockingRule(Array.Empty<FirebugManager.FirewallRuleView>(), 17, "5353"));
        Assert.Null(FirebugManager.BlockingRule(null, 17, "5353"));
    }

    [Fact]
    public void BlockingRule_ignores_disabled_outbound_uncovering_and_allow_rules()
    {
        var rules = new[]
        {
            Rule(name: "Disabled", enabled: false, allow: false, protocol: 17, ports: "5353"),
            Rule(name: "Outbound", inbound: false, allow: false, protocol: 17, ports: "5353"),
            Rule(name: "Tcp", allow: false, protocol: 6, ports: "5353"),
            Rule(name: "Other port", allow: false, protocol: 17, ports: "1900"),
            Rule(name: "Allow", protocol: 17, ports: "5353"),
        };
        Assert.Null(FirebugManager.BlockingRule(rules, 17, "5353"));
        // Port-less is guarded like AnyRuleAllows: without ports every block would "cover".
        Assert.Null(FirebugManager.BlockingRule(new[] { Rule(allow: false, protocol: 17, ports: "*") }, 17, null));
    }
}
