using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace Firebug
{
    /// <summary>
    /// Windows Firewall and URL ACL configuration manager.
    /// Consolidates firewall configuration patterns from MBXHub, MBRC, Chromecast, and UPnP projects.
    /// </summary>
    public class FirebugManager
    {
        /// <summary>
        /// Check if Windows Firewall is enabled on any profile.
        /// </summary>
        public bool IsFirewallEnabled()
        {
            // Registry first: netsh prints localized state words ("EIN", "Activé"),
            // so the old Contains("ON") match returned FALSE on non-English Windows —
            // which silently skipped every rule check downstream (review 2026-08-22).
            // EnableFirewall under each profile key is locale-independent.
            try
            {
                using (var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy"))
                {
                    if (baseKey != null)
                    {
                        foreach (var profile in new[] { "DomainProfile", "StandardProfile", "PublicProfile" })
                        {
                            using (var k = baseKey.OpenSubKey(profile))
                            {
                                if (k?.GetValue("EnableFirewall") is int v && v == 1) return true;
                            }
                        }
                        return false;
                    }
                }
            }
            catch { /* fall through to netsh */ }

            try
            {
                var output = RunNetshWithOutput("advfirewall show allprofiles state");
                return output.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// PURE: does this `netsh http show urlacl` output prove a reservation of
        /// <paramref name="url"/> that actually grants LISTEN broadly enough for our
        /// listeners? Decides on the SDDL line, which netsh prints untranslated on any
        /// locale: an allow ACE with GX (or GA) for Everyone (WD / S-1-1-0),
        /// Authenticated Users (AU / S-1-5-11) or Users (BU).
        /// <para>The URL echo alone is NOT health: a reservation held by
        /// BUILTIN\Administrators reports "present" by echo while the interactive
        /// user's listener still gets Access-denied and falls back — the exact logged
        /// defect this feature was built to catch (review 2026-08-22, C1).</para>
        /// <para>Output with no SDDL lines at all (ancient netsh) falls back to the
        /// echo check — unknown health is treated as present rather than triggering a
        /// permanent false-MISSING.</para>
        /// </summary>
        public static bool ParseUrlAclHealthy(string netshOutput, string url)
        {
            if (string.IsNullOrEmpty(netshOutput) || string.IsNullOrEmpty(url)) return false;
            if (netshOutput.IndexOf(url, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (netshOutput.IndexOf("SDDL", StringComparison.OrdinalIgnoreCase) < 0)
                return true; // echo-only fallback: cannot judge grantee, do not false-red
            return System.Text.RegularExpressions.Regex.IsMatch(
                netshOutput,
                @"\(A;[^;]*;[^;]*G[AX][^;]*;;;(WD|AU|BU|S-1-1-0|S-1-5-11)\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Does a rule of this name actually let inbound traffic in?
        ///
        /// <para>This used to be <c>output.Contains(ruleName)</c> — presence only. A rule
        /// that existed but was <c>Enabled: No</c>, or <c>Action: Block</c>, or outbound,
        /// reported PRESENT; consumers whose repair contract is "add only if absent" then
        /// left it alone forever, and every status surface said healthy while the port was
        /// shut. Same defect as the grantee-blind URL ACL check above, one layer over
        /// (review 2026-08-22, R3).</para>
        /// </summary>
        public bool HasRule(string ruleName) => HasRuleForPorts(ruleName, null, null);

        /// <summary>
        /// The same health question, narrowed to the ports the caller actually needs.
        ///
        /// <para>A rule matched by NAME alone survives any port change: after moving a
        /// listener the stale rule still exists, still reports present, and repair still
        /// leaves it — so the new port stays blocked while everything claims to be fine
        /// (review 2026-08-22, R4). Pass the ports and the answer means what the caller
        /// thinks it means.</para>
        /// </summary>
        /// <param name="ruleName">Rule name, matched case-insensitively.</param>
        /// <param name="protocol">"TCP", "UDP", or null for either.</param>
        /// <param name="ports">Comma-separated ports the rule must cover (e.g. "8080,8081"),
        /// or null to ask only whether an enabled inbound allow rule exists.</param>
        public bool HasRuleForPorts(string ruleName, string protocol, string ports)
        {
            var rules = TryEnumerateRules();
            if (rules != null)
                return RulesAllow(rules, ruleName, ProtocolNumber(protocol), ports);

            // COM unavailable (locked-down box, service stopped). Fall back to the old
            // presence check rather than false-red — same posture as IsFirewallEnabled.
            try
            {
                var output = RunNetshWithOutput($"advfirewall firewall show rule name=\"{ruleName}\"");
                return output.IndexOf(ruleName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The ports a rule ALREADY admits, so a caller can add to them instead of
        /// replacing them. Empty string when the rule does not exist, admits nothing, or
        /// cannot be read.
        ///
        /// <para>Exists because "repair this rule" was implemented as remove-then-add with
        /// only the caller's own ports, which silently evicts everybody else's. Lived case
        /// (2026-08-24): two MBXHub installs on one machine sharing the rule name "MBXHub"
        /// — repairing one removed the other's ports, so each install's startup check then
        /// reported the rule missing and repaired it back. A loop that cannot terminate
        /// and quietly breaks LAN access on alternate runs.</para>
        ///
        /// <para>Returns the union across every matching enabled inbound allow rule of the
        /// requested protocol: Windows permits several rules of one name, and a caller
        /// asking "what is already open" wants all of it, not the first match.</para>
        /// </summary>
        /// <param name="ruleName">Rule name, matched case-insensitively.</param>
        /// <param name="protocol">"TCP" or "UDP".</param>
        public string GetRulePorts(string ruleName, string protocol)
        {
            var rules = TryEnumerateRules();
            if (rules == null) return string.Empty;   // COM unavailable — say nothing, never guess

            int wanted = ProtocolNumber(protocol);
            var ports = new List<string>();
            foreach (var r in rules)
            {
                if (!string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!r.Enabled || !r.Inbound || !r.Allow) continue;
                if (wanted != 0 && r.Protocol != wanted) continue;
                if (string.IsNullOrWhiteSpace(r.LocalPorts)) continue;

                foreach (var p in r.LocalPorts.Split(','))
                {
                    var t = p.Trim();
                    // Tokens are preserved VERBATIM, ranges ("5000-5010") included.
                    // Dropping a range would narrow a rule that was wider than the caller
                    // thinks, and netsh accepts ranges in a localport list, so passing one
                    // straight back through a merge is both safe and correct.
                    //
                    // "*" cannot reach a merge in practice: PortsCovered() reports a
                    // wide-open rule as already covering everything, so the caller's
                    // health check passes and it never repairs. It is preserved here for
                    // the same reason as a range — this method reports what IS, and
                    // narrowing is the caller's mistake to avoid, not ours to hide.
                    if (t.Length == 0) continue;
                    if (!ports.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                        ports.Add(t);
                }
            }
            return string.Join(",", ports);
        }

        /// <summary>
        /// Union of two comma-separated port lists, order-stable, duplicates removed.
        /// Pure, so the merge that decides what a repair writes is testable without a
        /// firewall.
        /// </summary>
        public static string MergePorts(string existingPorts, string wantedPorts)
        {
            var result = new List<string>();
            foreach (var source in new[] { existingPorts, wantedPorts })
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                foreach (var p in source.Split(','))
                {
                    var t = p.Trim();
                    if (t.Length == 0) continue;
                    if (!result.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                        result.Add(t);
                }
            }
            return string.Join(",", result);
        }

        /// <summary>One firewall rule reduced to the fields that decide whether traffic
        /// actually gets in. Exists so the decision is a pure function that can be tested
        /// without the COM enumeration behind it.</summary>
        public readonly struct FirewallRuleView
        {
            public string Name { get; }
            public bool Enabled { get; }
            public bool Inbound { get; }
            public bool Allow { get; }
            /// <summary>6 = TCP, 17 = UDP, 256 = any.</summary>
            public int Protocol { get; }
            /// <summary>netsh/COM form: "8080", "8080,8081", "5000-5010", "*".</summary>
            public string LocalPorts { get; }

            public FirewallRuleView(string name, bool enabled, bool inbound, bool allow, int protocol, string localPorts)
            {
                Name = name; Enabled = enabled; Inbound = inbound;
                Allow = allow; Protocol = protocol; LocalPorts = localPorts;
            }
        }

        /// <summary>
        /// Does this rule set let <paramref name="wantedPorts"/> in under
        /// <paramref name="ruleName"/>? Windows permits several rules to share a name (one
        /// per profile is routine), so every match is considered — and an enabled inbound
        /// BLOCK is decisive, because in Windows block wins over allow no matter what else
        /// is present.
        /// </summary>
        public static bool RulesAllow(IEnumerable<FirewallRuleView> rules, string ruleName, int protocol, string wantedPorts)
        {
            if (rules == null || string.IsNullOrEmpty(ruleName)) return false;

            var allowed = false;
            foreach (var r in rules)
            {
                if (!string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!r.Enabled || !r.Inbound) continue;
                if (!ProtocolMatches(r.Protocol, protocol)) continue;
                if (!PortsCovered(r.LocalPorts, wantedPorts)) continue;
                if (!r.Allow) return false;   // block wins, whatever else says
                allowed = true;
            }
            return allowed;
        }

        /// <summary>256 (any) on either side matches everything; otherwise exact.</summary>
        internal static bool ProtocolMatches(int ruleProtocol, int wanted)
            => wanted <= 0 || wanted == 256 || ruleProtocol == 256 || ruleProtocol == wanted;

        internal static int ProtocolNumber(string protocol)
        {
            if (string.IsNullOrWhiteSpace(protocol)) return 256;
            if (protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return 6;
            if (protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)) return 17;
            return 256;
        }

        /// <summary>
        /// Does <paramref name="rulePorts"/> cover every port in <paramref name="wantedPorts"/>?
        /// Handles the netsh/COM forms: "*", a list, and ranges ("5000-5010").
        /// A caller that passes no wanted ports is asking only about existence, so that is true.
        /// A non-numeric token in the rule (netsh's "RPC", "Teredo", …) covers nothing —
        /// it cannot be shown to include the port, and reporting healthy on a rule we cannot
        /// read is the failure mode this whole change exists to remove.
        /// </summary>
        public static bool PortsCovered(string rulePorts, string wantedPorts)
        {
            if (string.IsNullOrWhiteSpace(wantedPorts)) return true;
            if (string.IsNullOrWhiteSpace(rulePorts)) return false;
            if (rulePorts.Trim() == "*") return true;

            foreach (var wantedToken in wantedPorts.Split(','))
            {
                if (!int.TryParse(wantedToken.Trim(), out var wanted)) continue;
                var covered = false;
                foreach (var ruleToken in rulePorts.Split(','))
                {
                    var t = ruleToken.Trim();
                    if (t == "*") { covered = true; break; }
                    var dash = t.IndexOf('-');
                    if (dash > 0)
                    {
                        if (int.TryParse(t.Substring(0, dash).Trim(), out var lo)
                            && int.TryParse(t.Substring(dash + 1).Trim(), out var hi)
                            && wanted >= lo && wanted <= hi) { covered = true; break; }
                    }
                    else if (int.TryParse(t, out var p) && p == wanted) { covered = true; break; }
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// Read the rule set through the Windows Firewall COM API. Deliberately NOT netsh:
        /// its output labels ("Enabled", "Yes", "Allow") are localized, and parsing them is
        /// exactly the mistake that made the old firewall-state check return the wrong
        /// answer on non-English Windows. The COM properties are typed and locale-free.
        /// Returns null when COM is unavailable, so the caller can fall back.
        /// </summary>
        private static List<FirewallRuleView> TryEnumerateRules()
        {
            try
            {
                var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null) return null;
                var policy = Activator.CreateInstance(policyType);
                if (policy == null) return null;

                var rules = policyType.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null);
                if (rules is not System.Collections.IEnumerable list) return null;

                var views = new List<FirewallRuleView>();
                foreach (var rule in list)
                {
                    if (rule == null) continue;
                    try
                    {
                        var t = rule.GetType();
                        views.Add(new FirewallRuleView(
                            Get(t, rule, "Name") as string,
                            Get(t, rule, "Enabled") is bool en && en,
                            Convert.ToInt32(Get(t, rule, "Direction")) == 1,   // NET_FW_RULE_DIR_IN
                            Convert.ToInt32(Get(t, rule, "Action")) == 1,      // NET_FW_ACTION_ALLOW
                            Convert.ToInt32(Get(t, rule, "Protocol")),
                            Get(t, rule, "LocalPorts") as string));
                    }
                    catch { /* one unreadable rule must not blind the whole sweep */ }
                }
                return views;
            }
            catch
            {
                return null;
            }
        }

        private static object Get(Type t, object target, string property)
            => t.InvokeMember(property, BindingFlags.GetProperty, null, target, null);

        /// <summary>
        /// Check if URL ACL exists for the specified port.
        /// </summary>
        public bool HasUrlAcl(int port)
        {
            try
            {
                var output = RunNetshWithOutput($"http show urlacl url=http://+:{port}/");
                return ParseUrlAclHealthy(output, $"http://+:{port}/");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check an EXPLICIT prefix reservation (e.g. http://127.0.0.1:8081/overlay/).
        /// The missing half of the ForUrl pair: <see cref="AddUrlAclForUrl"/> shipped
        /// 2026-08-18 so path-scoped reservations could be CREATED, but nothing could
        /// CHECK one — which is why no status surface anywhere could report the overlay
        /// bridge's reservation as absent (lived 2026-08-22).
        /// </summary>
        public bool HasUrlAclForUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var output = RunNetshWithOutput($"http show urlacl url={url}");
                return ParseUrlAclHealthy(output, url);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if running with administrator privileges.
        /// </summary>
        public bool IsElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Add a TCP inbound firewall rule (deletes existing rule first).
        /// </summary>
        public bool AddTcpInboundRule(string ruleName, int port, Action<string> log = null)
        {
            log = log ?? (_ => { });

            // Delete existing rule first (ignore errors)
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"", _ => { });

            return AddTcpRule(ruleName, port.ToString(), log);
        }

        /// <summary>
        /// Add a TCP inbound firewall rule without deleting existing rules.
        /// Supports comma-separated ports (e.g., "8080,3000").
        /// </summary>
        public bool AddTcpRule(string ruleName, string ports, Action<string> log = null)
        {
            log = log ?? (_ => { });

            var result = RunNetsh(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={ports}",
                log);

            if (result)
                log($"Added firewall rule '{ruleName}' for TCP port(s) {ports}");
            else
                log($"Failed to add firewall rule '{ruleName}'");

            return result;
        }

        /// <summary>
        /// Add a UDP inbound firewall rule (deletes existing rule first).
        /// </summary>
        public bool AddUdpInboundRule(string ruleName, int port, Action<string> log = null)
        {
            log = log ?? (_ => { });

            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"", _ => { });

            return AddUdpRule(ruleName, port.ToString(), log);
        }

        /// <summary>
        /// Add a UDP inbound firewall rule without deleting existing rules.
        /// Supports comma-separated ports (e.g., "1900,45345").
        /// </summary>
        public bool AddUdpRule(string ruleName, string ports, Action<string> log = null)
        {
            log = log ?? (_ => { });

            var result = RunNetsh(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=UDP localport={ports}",
                log);

            if (result)
                log($"Added firewall rule '{ruleName}' for UDP port(s) {ports}");
            else
                log($"Failed to add firewall rule '{ruleName}'");

            return result;
        }

        /// <summary>
        /// Build the netsh arguments for a URL ACL reservation.
        /// A bare SID (e.g. "S-1-5-11" = Authenticated Users) MUST go through the
        /// <c>sddl=</c> parameter — netsh's <c>user=</c> expects an account NAME and
        /// cannot resolve a raw SID (it fails with "The parameter is incorrect"),
        /// which silently broke SID-based reservations. An account name stays on the
        /// <c>user=</c> path. The <c>GX</c> grant mask is Microsoft's documented form
        /// for URL reservation registration rights.
        /// </summary>
        public static string BuildUrlAclArgs(int port, string user)
            => BuildUrlAclArgsForUrl($"http://+:{port}/", user);

        /// <summary>
        /// The same reservation for an EXPLICIT prefix rather than a whole port.
        ///
        /// <para>Needed because http.sys does not let a weak-wildcard reservation
        /// (<c>http://+:8081/</c>) authorise a strong explicit-host registration
        /// (<c>http://127.0.0.1:8081/overlay/</c>): they are different bindings, so a
        /// loopback-bound listener needs its own reservation even on a port that is already
        /// reserved. Measured 2026-08-18 — MBXHub's overlay bridge could not bind at all
        /// without one.</para>
        /// </summary>
        public static string BuildUrlAclArgsForUrl(string url, string user)
        {
            var grantee = user ?? "Everyone";
            var isSid = grantee.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
            var acl = isSid
                ? $"sddl=D:(A;;GX;;;{grantee})"
                : $"user={grantee}";
            return $"http add urlacl url={url} {acl}";
        }

        /// <summary>Reserve one explicit prefix. Same semantics as
        /// <see cref="AddUrlAcl(int,string,Action{string})"/>, for a URL rather than a port.</summary>
        public bool AddUrlAclForUrl(string url, string user = "Everyone", Action<string> log = null)
        {
            log = log ?? (_ => { });

            var result = RunNetsh(BuildUrlAclArgsForUrl(url, user), log);

            if (result)
                log($"Added URL ACL for {url}");
            else
                log($"URL ACL for {url} may already exist or requires elevation");

            return result;
        }

        /// <summary>
        /// Add URL ACL reservation for HTTP listener. Accepts either an account name
        /// (e.g. "Everyone") or a SID (e.g. "S-1-5-11"); see <see cref="BuildUrlAclArgs"/>.
        /// </summary>
        public bool AddUrlAcl(int port, string user = "Everyone", Action<string> log = null)
        {
            log = log ?? (_ => { });

            var result = RunNetsh(BuildUrlAclArgs(port, user), log);

            if (result)
                log($"Added URL ACL for port {port}");
            else
                log($"URL ACL for port {port} may already exist or requires elevation");

            return result;
        }

        /// <summary>
        /// Remove a firewall rule by name.
        /// </summary>
        public bool RemoveRule(string ruleName, Action<string> log = null)
        {
            log = log ?? (_ => { });
            return RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"", log);
        }

        /// <summary>
        /// Remove URL ACL reservation.
        /// </summary>
        public bool RemoveUrlAcl(int port, Action<string> log = null)
        {
            log = log ?? (_ => { });
            return RunNetsh($"http delete urlacl url=http://+:{port}/", log);
        }

        /// <summary>Remove an explicit-prefix reservation — the ForUrl pair's remove
        /// half, so an uninstall undoes exactly what <see cref="AddUrlAclForUrl"/>
        /// created.</summary>
        public bool RemoveUrlAclForUrl(string url, Action<string> log = null)
        {
            log = log ?? (_ => { });
            return RunNetsh($"http delete urlacl url={url}", log);
        }

        /// <summary>
        /// Open Windows Firewall settings UI.
        /// </summary>
        public void OpenFirewallSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "wf.msc", UseShellExecute = true });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = "control", Arguments = "firewall.cpl", UseShellExecute = true });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo { FileName = "ms-settings:windowsdefender-firewall", UseShellExecute = true });
                }
            }
        }

        /// <summary>
        /// Generate a batch script for manual configuration.
        /// </summary>
        public string GenerateScript(string appName, int port)
        {
            return $@"@echo off
:: {appName} Firewall Configuration
:: Run this script as Administrator

echo Configuring {appName} for port {port}...

:: Add URL reservation (allows non-admin to bind to port)
netsh http add urlacl url=http://+:{port}/ user=Everyone

:: Add firewall rule
netsh advfirewall firewall delete rule name=""{appName}"" 2>nul
netsh advfirewall firewall add rule name=""{appName}"" dir=in action=allow protocol=TCP localport={port}

echo.
echo Done. {appName} can now accept connections on port {port}.
pause
";
        }

        private bool RunNetsh(string args, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0)
                    {
                        var error = process.StandardError.ReadToEnd();
                        log($"netsh failed: {error}".Trim());
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                log($"netsh failed: {ex.Message}");
                return false;
            }
        }

        private string RunNetshWithOutput(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                // Read BEFORE waiting: WaitForExit-then-ReadToEnd deadlocks when the
                // child fills the stdout pipe buffer (a full `netsh http show urlacl`
                // listing can) — the child blocks writing, we block waiting
                // (review 2026-08-22, minor set).
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);
                return output;
            }
        }
    }
}
