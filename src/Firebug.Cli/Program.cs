using System;
using System.Diagnostics;
using System.Security.Principal;
using Firebug;

namespace Firebug.Cli
{
    /// <summary>
    /// Firebug CLI - Windows Firewall configuration utility.
    /// Self-elevates only when needed for add/remove operations.
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();

            // Commands that require elevation
            if (command == "add" || command == "remove")
            {
                if (!IsElevated())
                {
                    return RelaunchElevated(args);
                }
            }

            var fb = new FirebugManager();

            switch (command)
            {
                case "add":
                    return HandleAdd(fb, args);

                case "remove":
                    return HandleRemove(fb, args);

                case "check":
                    return HandleCheck(fb, args);

                case "status":
                    return HandleStatus(fb);

                case "open":
                    fb.OpenFirewallSettings();
                    return 0;

                default:
                    Console.WriteLine($"Unknown command: {command}");
                    ShowUsage();
                    return 1;
            }
        }

        static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        static int RelaunchElevated(string[] args)
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;

            // Quote args that contain spaces
            var quotedArgs = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                quotedArgs[i] = args[i].Contains(" ") ? $"\"{args[i]}\"" : args[i];
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", quotedArgs),
                Verb = "runas",
                UseShellExecute = true
            };

            try
            {
                var process = Process.Start(startInfo);
                process?.WaitForExit();
                return process?.ExitCode ?? 1;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User cancelled UAC
                Console.WriteLine("Elevation required. Operation cancelled.");
                return 1;
            }
        }

        static void ShowUsage()
        {
            Console.WriteLine("Firebug - Windows Firewall Configuration");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  firebug add --name <AppName> --port <Port> [--protocol tcp|udp] [--urlacl]");
            Console.WriteLine("  firebug add --name <AppName> --tcp <Ports> --udp <Ports> [--urlacl <Ports>]");
            Console.WriteLine("  firebug remove --name <AppName> [--port <Port>]");
            Console.WriteLine("  firebug check --name <AppName> [--port <Port>]");
            Console.WriteLine("  firebug status");
            Console.WriteLine("  firebug open");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  firebug add --name MyApp --port 8080 --urlacl");
            Console.WriteLine("  firebug add --name MyApp --tcp 8080,3000 --udp 1900 --urlacl 8080");
            Console.WriteLine("  firebug remove --name MyApp --port 8080");
            Console.WriteLine("  firebug check --name MyApp --port 8080");
        }

        static int HandleAdd(FirebugManager fb, string[] args)
        {
            string name = null;
            int port = 0;
            string protocol = "tcp";
            bool urlacl = false;
            string tcpPorts = null;
            string udpPorts = null;
            string urlAclPorts = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--name":
                    case "-n":
                        if (i + 1 < args.Length) name = args[++i];
                        break;
                    case "--port":
                    case "-p":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                    case "--protocol":
                        if (i + 1 < args.Length) protocol = args[++i].ToLowerInvariant();
                        break;
                    case "--urlacl":
                        // Check if next arg is a port list or just a flag
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            urlAclPorts = args[++i];
                        else
                            urlacl = true;
                        break;
                    case "--tcp":
                        if (i + 1 < args.Length) tcpPorts = args[++i];
                        break;
                    case "--udp":
                        if (i + 1 < args.Length) udpPorts = args[++i];
                        break;
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("Error: --name is required");
                return 1;
            }

            // Multi-port mode: --tcp and/or --udp specified
            if (!string.IsNullOrEmpty(tcpPorts) || !string.IsNullOrEmpty(udpPorts))
            {
                return HandleMultiPortAdd(fb, name, tcpPorts, udpPorts, urlAclPorts);
            }

            // Single port mode (legacy)
            if (port <= 0)
            {
                Console.WriteLine("Error: --port is required (or use --tcp/--udp for multiple ports)");
                return 1;
            }

            Console.WriteLine($"Adding firewall rule '{name}' for {protocol.ToUpper()} port {port}...");

            bool success;
            if (protocol == "udp")
                success = fb.AddUdpInboundRule(name, port, Console.WriteLine);
            else
                success = fb.AddTcpInboundRule(name, port, Console.WriteLine);

            if (urlacl)
            {
                Console.WriteLine($"Adding URL ACL for port {port}...");
                fb.AddUrlAcl(port, "Everyone", Console.WriteLine);
            }

            return success ? 0 : 1;
        }

        static int HandleMultiPortAdd(FirebugManager fb, string name, string tcpPorts, string udpPorts, string urlAclPorts)
        {
            bool allSuccess = true;

            // Delete existing rules once at the start
            Console.WriteLine($"Removing existing '{name}' rules...");
            fb.RemoveRule(name, _ => { });

            // Add TCP rule with comma-separated ports (single rule)
            if (!string.IsNullOrEmpty(tcpPorts))
            {
                Console.WriteLine($"Adding TCP firewall rule '{name}' for port(s) {tcpPorts}...");
                if (!fb.AddTcpRule(name, tcpPorts, Console.WriteLine))
                    allSuccess = false;
            }

            // Add UDP rule with comma-separated ports (single rule)
            if (!string.IsNullOrEmpty(udpPorts))
            {
                Console.WriteLine($"Adding UDP firewall rule '{name}' for port(s) {udpPorts}...");
                if (!fb.AddUdpRule(name, udpPorts, Console.WriteLine))
                    allSuccess = false;
            }

            // Add URL ACLs (each port needs its own reservation)
            if (!string.IsNullOrEmpty(urlAclPorts))
            {
                foreach (var portStr in urlAclPorts.Split(','))
                {
                    if (int.TryParse(portStr.Trim(), out int p) && p > 0)
                    {
                        Console.WriteLine($"Adding URL ACL for port {p}...");
                        fb.AddUrlAcl(p, "Everyone", Console.WriteLine);
                    }
                }
            }

            Console.WriteLine(allSuccess ? "All rules added successfully." : "Some rules failed.");
            return allSuccess ? 0 : 1;
        }

        static int HandleRemove(FirebugManager fb, string[] args)
        {
            string name = null;
            int port = 0;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--name":
                    case "-n":
                        if (i + 1 < args.Length) name = args[++i];
                        break;
                    case "--port":
                    case "-p":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("Error: --name is required");
                return 1;
            }

            Console.WriteLine($"Removing firewall rule '{name}'...");
            fb.RemoveRule(name, Console.WriteLine);

            if (port > 0)
            {
                Console.WriteLine($"Removing URL ACL for port {port}...");
                fb.RemoveUrlAcl(port, Console.WriteLine);
            }

            Console.WriteLine("Done.");
            return 0;
        }

        static int HandleCheck(FirebugManager fb, string[] args)
        {
            string name = null;
            int port = 0;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--name":
                    case "-n":
                        if (i + 1 < args.Length) name = args[++i];
                        break;
                    case "--port":
                    case "-p":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("Error: --name is required");
                return 1;
            }

            var hasRule = fb.HasRule(name);
            Console.WriteLine($"Firewall rule '{name}': {(hasRule ? "FOUND" : "NOT FOUND")}");

            if (port > 0)
            {
                var hasAcl = fb.HasUrlAcl(port);
                Console.WriteLine($"URL ACL (port {port}): {(hasAcl ? "FOUND" : "NOT FOUND")}");
            }

            return hasRule ? 0 : 1;
        }

        static int HandleStatus(FirebugManager fb)
        {
            Console.WriteLine("Firebug Status");
            Console.WriteLine();

            var enabled = fb.IsFirewallEnabled();
            Console.WriteLine($"Windows Firewall: {(enabled ? "ENABLED" : "DISABLED")}");

            var elevated = fb.IsElevated();
            Console.WriteLine($"Running Elevated: {(elevated ? "YES" : "NO")}");

            return 0;
        }
    }
}
