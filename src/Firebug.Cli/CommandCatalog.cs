#nullable enable
using System;
using System.Linq;

namespace Firebug.Cli
{
    /// <summary>
    /// One flag of a CLI verb. Plain net48-safe shapes on purpose (no records,
    /// no init setters) — this catalog is the piece the cross-repo completion
    /// spec expects other tools (truedat) to copy, and their ports must not
    /// inherit polyfill baggage.
    /// </summary>
    public sealed class Flag
    {
        public string Long { get; }
        public string? Short { get; }
        public bool TakesValue { get; }
        public string Help { get; }

        public Flag(string longName, string? shortName, bool takesValue, string help)
        {
            Long = longName; Short = shortName; TakesValue = takesValue; Help = help;
        }
    }

    /// <summary>One CLI verb and its flags.</summary>
    public sealed class Command
    {
        public string Name { get; }
        public Flag[] Flags { get; }
        public string Help { get; }

        public Command(string name, Flag[] flags, string help)
        {
            Name = name; Flags = flags; Help = help;
        }
    }

    /// <summary>
    /// Machine-readable catalog of the CLI's command surface — the single
    /// source of truth the completion emitter, the interactive completer, and
    /// the parity self-test all read. The hand parsers in Program.cs keep
    /// working as-is (v1 per the spec); CommandCatalogTests asserts BOTH
    /// directions so the two cannot drift silently.
    /// </summary>
    public static class CommandCatalog
    {
        public static readonly Command[] Commands =
        {
            new Command("add", new[]
            {
                new Flag("--name", "-n", true, "App / rule name"),
                new Flag("--port", "-p", true, "Single port"),
                new Flag("--protocol", null, true, "tcp or udp"),
                new Flag("--urlacl", null, false, "Also reserve URL ACL(s); optionally takes a port list"),
                new Flag("--tcp", null, true, "Comma-separated TCP ports"),
                new Flag("--udp", null, true, "Comma-separated UDP ports"),
            }, "Add firewall rule(s) and optional URL ACLs"),

            new Command("check", new[]
            {
                new Flag("--name", "-n", true, "App / rule name"),
                new Flag("--port", "-p", true, "Also check the URL ACL for this port"),
            }, "Check whether a rule (and optionally a URL ACL) exists"),

            new Command("completion", Array.Empty<Flag>(),
                "Print a shell tab-completion script (subcommand: powershell)"),

            new Command("help", Array.Empty<Flag>(), "Show command help"),

            new Command("open", Array.Empty<Flag>(), "Open Windows Firewall settings"),

            new Command("pick", new[]
            {
                new Flag("--preferred", null, true, "Preferred starting port (default 8000)"),
                new Flag("--saved", null, true, "Reuse this previously saved port when still free"),
                new Flag("--pair", null, false, "Pick an adjacent free pair (server + side-channel)"),
                new Flag("--verbose", "-v", false, "Show the probe walk"),
            }, "Pick a free port (prints parseable PORT:/SIDE: lines)"),

            new Command("remove", new[]
            {
                new Flag("--name", "-n", true, "App / rule name"),
                new Flag("--port", "-p", true, "Also remove the URL ACL for this port"),
            }, "Remove firewall rule(s) and optionally a URL ACL"),

            new Command("reserve", new[]
            {
                new Flag("--name", "-n", true, "App / rule name"),
                new Flag("--port", "-p", true, "Port to reserve"),
                new Flag("--protocol", null, true, "tcp or udp (udp is rule-only)"),
                new Flag("--pair", null, false, "Reserve the port and port+1"),
                new Flag("--no-urlacl", null, false, "Firewall rule only, skip the URL ACL"),
                new Flag("--pick", null, false, "Pick a free port first (combined flow)"),
                new Flag("--preferred", null, true, "With --pick: preferred starting port"),
                new Flag("--saved", null, true, "With --pick: reuse this saved port when still free"),
                new Flag("--verbose", "-v", false, "Show the pick walk"),
            }, "Firewall rule + URL ACL for a port in one step"),

            new Command("scan", new[]
            {
                new Flag("--target", "-t", true, "SSDP search target (default ssdp:all)"),
                new Flag("--mdns", null, true, "mDNS/DNS-SD sweep for these service types instead"),
                new Flag("--duration", "-d", true, "Listen window in ms (default 3000)"),
                new Flag("--raw", null, false, "Skip the description fetch (wire-level sweep)"),
                new Flag("--verbose", "-v", false, "Full wire log (SsdpTrace)"),
            }, "Discover devices on the LAN (SSDP or mDNS)"),

            new Command("status", Array.Empty<Flag>(), "Show firewall and elevation status"),
        };

        public static Command? Find(string name)
        {
            foreach (var c in Commands)
                if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
            return null;
        }
    }
}
