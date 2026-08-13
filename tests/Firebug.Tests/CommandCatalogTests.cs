using System.Text.RegularExpressions;
using Firebug.Cli;

namespace Firebug.Tests;

/// <summary>
/// The parity self-test from the cross-repo completion spec: the catalog and
/// the hand parsers in Program.cs must agree in BOTH directions, and the
/// emitted PowerShell script must carry the whole surface. The parser
/// direction is a source-level assertion (the CLI is a net48 exe the net8
/// test host cannot execute), which the spec explicitly allows.
/// </summary>
public class CommandCatalogTests
{
    private static string ProgramSource()
    {
        // Walk up from the test bin until the repo layout appears.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Firebug.Cli", "Program.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Program.cs not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_catalog_verb_is_a_parser_case()
    {
        var src = ProgramSource();
        foreach (var c in CommandCatalog.Commands)
            Assert.Contains($"case \"{c.Name}\":", src);
    }

    [Fact]
    public void Every_catalog_flag_is_a_parser_case()
    {
        var src = ProgramSource();
        foreach (var c in CommandCatalog.Commands)
            foreach (var f in c.Flags)
            {
                Assert.Contains($"case \"{f.Long}\":", src);
                if (f.Short != null) Assert.Contains($"case \"{f.Short}\":", src);
            }
    }

    [Fact]
    public void Every_parser_long_flag_is_in_the_catalog()
    {
        var src = ProgramSource();
        var catalogLongs = CommandCatalog.Commands
            .SelectMany(c => c.Flags).Select(f => f.Long).ToHashSet(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, "case \"(--[a-z-]+)\":"))
            Assert.Contains(m.Groups[1].Value, catalogLongs);
    }

    [Fact]
    public void Every_parser_verb_case_is_in_the_catalog()
    {
        var src = ProgramSource();
        var names = CommandCatalog.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        // Verb cases are the bare-lowercase case labels (flags start with '-').
        foreach (Match m in Regex.Matches(src, "case \"([a-z][a-z-]*)\":"))
            Assert.Contains(m.Groups[1].Value, names);
    }

    [Fact]
    public void Catalog_names_are_distinct_and_flags_distinct_per_verb()
    {
        var names = CommandCatalog.Commands.Select(c => c.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var c in CommandCatalog.Commands)
        {
            var longs = c.Flags.Select(f => f.Long).ToArray();
            Assert.Equal(longs.Length, longs.Distinct(StringComparer.Ordinal).Count());
        }
    }

    // --- The emitter carries the whole surface ------------------------------

    [Fact]
    public void Emitted_script_contains_every_verb_and_flag()
    {
        var script = CompletionEmitter.EmitPowerShell();
        Assert.Contains("Register-ArgumentCompleter", script);
        foreach (var c in CommandCatalog.Commands)
        {
            Assert.Contains($"'{c.Name}'", script);
            foreach (var f in c.Flags)
            {
                Assert.Contains($"'{f.Long}'", script);
                if (f.Short != null) Assert.Contains($"'{f.Short}'", script);
            }
        }
    }

    [Fact]
    public void Emitted_script_is_balanced_and_ascii()
    {
        var script = CompletionEmitter.EmitPowerShell();
        Assert.Equal(script.Count(ch => ch == '{'), script.Count(ch => ch == '}'));
        Assert.Equal(script.Count(ch => ch == '('), script.Count(ch => ch == ')'));
        Assert.DoesNotContain(script, ch => ch > 127);
    }
}
