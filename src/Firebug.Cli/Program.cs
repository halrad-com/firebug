using System;
using System.Diagnostics;
using System.Security.Principal;
using Firebug;
using SsdpCore;

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

                case "scan":
                    return HandleScan(args);

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
            Console.WriteLine("  firebug scan [--target <st>] [--duration <ms>] [--raw] [--verbose]");
            Console.WriteLine("  firebug scan --mdns <type[,type]> [--duration <ms>] [--verbose]");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  firebug add --name MyApp --port 8080 --urlacl");
            Console.WriteLine("  firebug add --name MyApp --tcp 8080,3000 --udp 1900 --urlacl 8080");
            Console.WriteLine("  firebug remove --name MyApp --port 8080");
            Console.WriteLine("  firebug check --name MyApp --port 8080");
            Console.WriteLine("  firebug scan");
            Console.WriteLine("  firebug scan --target urn:schemas-upnp-org:device:MediaRenderer:1");
            Console.WriteLine("  firebug scan --mdns _airplay._tcp,_devialet._tcp");
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

        /// <summary>
        /// Network discovery scan — the worked example for SsdpCore's client
        /// side. SSDP by default (results come back NAMED via the description
        /// fetch); --mdns switches to a DNS-SD sweep. No elevation needed.
        /// Exit code: 0 = something answered, 1 = nothing found.
        /// </summary>
        static int HandleScan(string[] args)
        {
            string target = SsdpConstants.SearchTargetAll;
            string mdnsTypes = null;
            int durationMs = 3000;
            bool raw = false;
            bool verbose = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--target":
                    case "-t":
                        if (i + 1 < args.Length) target = args[++i];
                        break;
                    case "--mdns":
                        if (i + 1 < args.Length) mdnsTypes = args[++i];
                        break;
                    case "--duration":
                    case "-d":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out durationMs);
                        break;
                    case "--raw":
                        raw = true;
                        break;
                    case "--verbose":
                    case "-v":
                        verbose = true;
                        break;
                }
            }
            if (durationMs <= 0) durationMs = 3000;

            // The SsdpTrace switch in action: quiet by default (warnings and
            // hard errors still print — errors always bypass the switch),
            // everything with --verbose.
            SsdpTrace.Switch.Level = verbose ? TraceLevel.Verbose : TraceLevel.Warning;
            Action<TraceLevel, string> log = (level, msg) => Console.WriteLine($"  [{level}] {msg}");

            if (mdnsTypes != null)
            {
                var types = mdnsTypes.Split(',');
                Console.WriteLine($"mDNS scan for [{string.Join(", ", types)}] ({durationMs} ms)...");
                using (var scanner = new MdnsScanner(log))
                {
                    var services = scanner.ScanAsync(types, durationMs).GetAwaiter().GetResult();
                    Console.WriteLine();
                    foreach (var s in services)
                        Console.WriteLine($"  {s.IPAddress,-16} {s.Name,-32} {s.ServiceType}  :{s.Port}");
                    Console.WriteLine();
                    Console.WriteLine($"{services.Count} service(s) found.");
                    return services.Count > 0 ? 0 : 1;
                }
            }

            Console.WriteLine($"SSDP scan for {target} ({durationMs} ms){(raw ? ", raw (no descriptions)" : "")}...");
            using (var scanner = new SsdpScanner(log))
            {
                var devices = scanner.ScanAsync(target, durationMs, fetchDescriptions: !raw)
                    .GetAwaiter().GetResult();
                Console.WriteLine();
                foreach (var d in devices)
                {
                    var name = string.IsNullOrEmpty(d.FriendlyName) ? "(unnamed)" : d.FriendlyName;
                    var model = string.IsNullOrEmpty(d.ModelName) ? "" : $"  {d.ModelName}";
                    var maker = string.IsNullOrEmpty(d.Manufacturer) ? "" : $"  ({d.Manufacturer})";
                    Console.WriteLine($"  {d.Address,-16} {name}{model}{maker}");
                    Console.WriteLine($"                   {d.DeviceType}");
                    Console.WriteLine($"                   {d.Location}");
                }
                Console.WriteLine();
                Console.WriteLine($"{devices.Count} device(s) found.");
                return devices.Count > 0 ? 0 : 1;
            }
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
