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
                // Bare invocation in a real console drops into the interactive
                // prompt (Tab completes verbs, Up/Down history). Redirected
                // stdin/stdout keeps the old contract: usage + exit 1, so
                // scripts that misfire stay loud instead of hanging on ReadKey.
                if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
                    return RunInteractive();
                ShowUsage();
                return 1;
            }

            return Run(args);
        }

        /// <summary>
        /// Interactive console — the worked example for the LineEditor /
        /// ICompleter pattern (see LineEditor.cs). Each submitted line runs
        /// through the same verb dispatch as a normal invocation, so add and
        /// remove still self-elevate per command.
        /// </summary>
        static int RunInteractive()
        {
            Console.WriteLine("Firebug interactive — Tab completes verbs, Up/Down history, 'quit' to exit.");
            var editor = new LineEditor(new VerbCompleter());
            while (true)
            {
                var line = editor.ReadLine("firebug> ");
                if (line == null) return 0;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "quit" || trimmed == "exit") return 0;
                Run(SplitArgs(trimmed));
            }
        }

        /// <summary>Split a command line into args, honoring double quotes.</summary>
        static string[] SplitArgs(string line)
        {
            var result = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (var c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result.ToArray();
        }

        static int Run(string[] args)
        {
            if (args.Length == 0) { ShowUsage(); return 1; }

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

                case "pick":
                    return HandlePick(args);

                case "reserve":
                    // Elevation handled inside AFTER validation, so bad args
                    // fail fast instead of triggering a pointless UAC prompt.
                    return HandleReserve(fb, args);

                case "help":
                    ShowUsage();
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
            Console.WriteLine("  firebug scan [--target <st>] [--duration <ms>] [--raw] [--verbose]");
            Console.WriteLine("  firebug scan --mdns <type[,type]> [--duration <ms>] [--verbose]");
            Console.WriteLine("  firebug pick [--preferred <port>] [--saved <port>] [--pair] [--verbose]");
            Console.WriteLine("  firebug reserve --name <AppName> --port <Port> [--protocol tcp|udp] [--pair] [--no-urlacl]");
            Console.WriteLine("  firebug reserve --name <AppName> --pick [--preferred <port>] [--saved <port>] [--pair]");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  firebug add --name MyApp --port 8080 --urlacl");
            Console.WriteLine("  firebug add --name MyApp --tcp 8080,3000 --udp 1900 --urlacl 8080");
            Console.WriteLine("  firebug remove --name MyApp --port 8080");
            Console.WriteLine("  firebug check --name MyApp --port 8080");
            Console.WriteLine("  firebug scan");
            Console.WriteLine("  firebug scan --target urn:schemas-upnp-org:device:MediaRenderer:1");
            Console.WriteLine("  firebug scan --mdns _airplay._tcp,_devialet._tcp");
            Console.WriteLine("  firebug pick --preferred 8080");
            Console.WriteLine("  firebug reserve --name MyApp --port 8080");
            Console.WriteLine("  firebug reserve --name MyApp --pick --preferred 8080");
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

        /// <summary>
        /// Shared pick core: choose a port (honoring a saved one when still
        /// free), print the parseable PORT:/SIDE: lines, and report whether the
        /// result is actually bindable. PortPicker returns the preferred value
        /// even when nothing is free — by design, so binds fail loudly — which
        /// is why the result is probed again for the exit code.
        /// </summary>
        static bool DoPick(int preferred, int saved, bool pair, bool verbose, out int port)
        {
            Action<string> log = verbose ? (s => Console.WriteLine("  " + s)) : (Action<string>)null;

            if (pair)
            {
                port = (saved > 0 && PortPicker.IsFree(saved) && PortPicker.IsFree(saved + 1))
                    ? saved
                    : PortPicker.PickPair(preferred, log: log);
            }
            else
            {
                port = PortPicker.Resolve(saved, preferred, log);
            }

            var ok = PortPicker.IsFree(port) && (!pair || PortPicker.IsFree(port + 1));
            Console.WriteLine($"PORT: {port}");
            if (pair) Console.WriteLine($"SIDE: {port + 1}");
            if (!ok) Console.WriteLine($"Warning: no free {(pair ? "pair" : "port")} found near {preferred} — {port} is busy.");
            return ok;
        }

        /// <summary>
        /// Pick a free port — PortPicker's worked example. Pure and unelevated;
        /// persist the printed port and 'firebug reserve' it. Exit 0 = the
        /// printed port is bindable right now, 1 = nothing free was found.
        /// </summary>
        static int HandlePick(string[] args)
        {
            int preferred = 8000, saved = 0;
            bool pair = false, verbose = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--preferred":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out preferred);
                        break;
                    case "--saved":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out saved);
                        break;
                    case "--pair":
                        pair = true;
                        break;
                    case "--verbose":
                    case "-v":
                        verbose = true;
                        break;
                }
            }
            if (preferred <= 0) preferred = 8000;

            return DoPick(preferred, saved, pair, verbose, out _) ? 0 : 1;
        }

        /// <summary>
        /// Reserve a port for an app: firewall rule + URL ACL in one verb — the
        /// authorize half of the pick/reserve flow. With --pick it first runs
        /// the pick (unelevated, so PORT: prints in the caller's console), then
        /// re-launches elevated with the CONCRETE port. Validation happens
        /// before any elevation so bad args never cost a UAC prompt.
        /// </summary>
        static int HandleReserve(FirebugManager fb, string[] args)
        {
            string name = null;
            int port = 0;
            string protocol = "tcp";
            bool pair = false, urlacl = true, pick = false, verbose = false;
            int preferred = 8000, saved = 0;

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
                    case "--pair":
                        pair = true;
                        break;
                    case "--no-urlacl":
                        urlacl = false;
                        break;
                    case "--pick":
                        pick = true;
                        break;
                    case "--preferred":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out preferred);
                        break;
                    case "--saved":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out saved);
                        break;
                    case "--verbose":
                    case "-v":
                        verbose = true;
                        break;
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("Error: --name is required");
                return 1;
            }
            if (pick)
            {
                if (!DoPick(preferred, saved, pair, verbose, out port))
                    return 1;   // nothing free — do not reserve a busy port
            }
            if (port <= 0)
            {
                Console.WriteLine("Error: --port is required (or use --pick)");
                return 1;
            }

            // URL ACLs are an http.sys concept; a UDP reservation is rule-only.
            if (protocol == "udp") urlacl = false;

            if (!IsElevated())
            {
                // Re-launch with the CONCRETE port — the elevated child must
                // never re-pick and land somewhere different from what the
                // caller just read off the PORT: line.
                var concrete = new System.Collections.Generic.List<string>
                    { "reserve", "--name", name, "--port", port.ToString(), "--protocol", protocol };
                if (pair) concrete.Add("--pair");
                if (!urlacl) concrete.Add("--no-urlacl");
                return RelaunchElevated(concrete.ToArray());
            }

            var ports = pair ? $"{port},{port + 1}" : port.ToString();
            Console.WriteLine($"Reserving {protocol.ToUpper()} port(s) {ports} for '{name}'...");

            // Idempotent: replace this app's rules rather than stacking duplicates
            // (same convention as the multi-port add path).
            fb.RemoveRule(name, _ => { });

            bool ok = protocol == "udp"
                ? (pair ? fb.AddUdpRule(name, ports, Console.WriteLine) : fb.AddUdpInboundRule(name, port, Console.WriteLine))
                : (pair ? fb.AddTcpRule(name, ports, Console.WriteLine) : fb.AddTcpInboundRule(name, port, Console.WriteLine));

            if (urlacl)
            {
                Console.WriteLine($"Adding URL ACL for port {port}...");
                fb.AddUrlAcl(port, "Everyone", Console.WriteLine);
                if (pair)
                {
                    Console.WriteLine($"Adding URL ACL for port {port + 1}...");
                    fb.AddUrlAcl(port + 1, "Everyone", Console.WriteLine);
                }
            }

            Console.WriteLine(ok ? "Reserved." : "Reserve failed.");
            return ok ? 0 : 1;
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
