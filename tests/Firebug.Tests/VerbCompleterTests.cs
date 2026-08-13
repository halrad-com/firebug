using Firebug.Cli;

namespace Firebug.Tests;

/// <summary>
/// Pins the completer and the live verb catalog. The catalog-count pin is the
/// drift guard: adding a verb to Program.Run without touching the catalog (or
/// vice versa) must fail a test, not ship silently.
/// </summary>
public class VerbCompleterTests
{
    [Fact]
    public void Catalog_count_is_the_drift_guard()
    {
        // Deliberately a literal: editing the catalog without pausing here is
        // exactly what this pin exists to prevent. Verbs = every Program.Run
        // switch case (add, check, help, open, pick, remove, reserve, scan,
        // status) plus the REPL-level 'quit'.
        Assert.Equal(10, FirebugVerbs.Names.Length);
    }

    [Fact]
    public void Catalog_names_are_distinct_and_lowercase()
    {
        Assert.Equal(FirebugVerbs.Names.Length, FirebugVerbs.Names.Distinct().Count());
        Assert.All(FirebugVerbs.Names, n => Assert.Equal(n.ToLowerInvariant(), n));
    }

    [Fact]
    public void Empty_input_returns_whole_catalog()
    {
        // The editor guards the empty buffer itself (no ghost, Tab inert) —
        // the completer stays uniform and returns everything.
        var c = new VerbCompleter();
        Assert.Equal(FirebugVerbs.Names.Length, c.Complete("").Count);
    }

    [Fact]
    public void Prefix_matches_in_ordinal_order()
    {
        var c = new VerbCompleter();
        var re = c.Complete("re");
        Assert.Equal(new[] { "remove", "reserve" }, re);
    }

    [Fact]
    public void No_match_returns_empty()
    {
        var c = new VerbCompleter();
        Assert.Empty(c.Complete("zzz"));
    }

    [Fact]
    public void Input_with_space_returns_empty()
    {
        var c = new VerbCompleter();
        Assert.Empty(c.Complete("scan --t"));
        Assert.Empty(c.Complete("reserve "));
    }
}
