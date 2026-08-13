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
        // switch case (add, check, completion, help, open, pick, remove,
        // reserve, scan, status) plus the REPL-level 'quit'.
        Assert.Equal(11, FirebugVerbs.Names.Length);
        Assert.Contains("completion", FirebugVerbs.Names);
        Assert.Contains("quit", FirebugVerbs.Names);
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

    // --- FirebugArgCompleter: the catalog-driven argument layer --------------

    [Fact]
    public void Arg_completer_completes_flags_as_whole_lines()
    {
        var c = new FirebugArgCompleter();
        Assert.Equal(
            new[] { "reserve --pair", "reserve --pick", "reserve --port", "reserve --preferred", "reserve --protocol" },
            c.Complete("reserve --p"));
    }

    [Fact]
    public void Arg_completer_offers_short_forms_too()
    {
        var c = new FirebugArgCompleter();
        Assert.Contains("scan -t", c.Complete("scan -t"));   // exact short form is its own candidate
        Assert.Contains("scan -v", c.Complete("scan -"));
    }

    [Fact]
    public void Arg_completer_is_silent_for_values_and_unknown_verbs()
    {
        var c = new FirebugArgCompleter();
        Assert.Empty(c.Complete("reserve --port 80"));   // value position
        Assert.Empty(c.Complete("bogus --p"));           // unknown verb
    }

    [Fact]
    public void Hint_shows_flag_grammar_right_after_the_verb()
    {
        var c = new FirebugArgCompleter();
        var hint = c.Hint("pick ");
        Assert.Equal("[--preferred <v>] [--saved <v>] [--pair] [--verbose]", hint);
        Assert.Equal("", c.Hint("pick"));        // no trailing space yet
        Assert.Equal("", c.Hint("pick --p "));   // already in flag territory
        Assert.Equal("", c.Hint("status "));     // verb with no flags
    }
}
