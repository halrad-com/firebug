using System;
using System.Diagnostics;
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
        /// Check if a firewall rule exists by name.
        /// </summary>
        public bool HasRule(string ruleName)
        {
            try
            {
                var output = RunNetshWithOutput($"advfirewall firewall show rule name=\"{ruleName}\"");
                return output.Contains(ruleName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if URL ACL exists for the specified port.
        /// </summary>
        public bool HasUrlAcl(int port)
        {
            try
            {
                var output = RunNetshWithOutput($"http show urlacl url=http://+:{port}/");
                return output.Contains($"http://+:{port}/");
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
        {
            var grantee = user ?? "Everyone";
            var isSid = grantee.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
            var acl = isSid
                ? $"sddl=D:(A;;GX;;;{grantee})"
                : $"user={grantee}";
            return $"http add urlacl url=http://+:{port}/ {acl}";
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
                process.WaitForExit(10000);
                return process.StandardOutput.ReadToEnd();
            }
        }
    }
}
