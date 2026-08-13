using Firebug.Cli;

namespace Firebug.Tests;

/// <summary>
/// Pins the pure line-editor state machine (LineEditorLogic.Step) — the
/// decision half of the interactive prompt. The console painter is
/// deliberately dumb and untested.
/// </summary>
public class LineEditorLogicTests
{
    private static readonly ICompleter Completer = new VerbCompleter();
    private static readonly IReadOnlyList<string> NoHistory = Array.Empty<string>();

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0')
        => new(ch, key, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Char(char ch)
        => new(ch, ConsoleKey.NoName, shift: false, alt: false, control: false);

    private static EditState Type(string text, EditState from)
    {
        var s = from;
        foreach (var c in text)
            (s, _) = LineEditorLogic.Step(s, Char(c), Completer, NoHistory);
        return s;
    }

    [Fact]
    public void Typing_builds_buffer_and_ghost()
    {
        var s = Type("sc", EditState.Empty);
        Assert.Equal("sc", s.Buffer);
        Assert.Equal("an", s.Ghost);   // "scan" is the only sc* verb
    }

    [Fact]
    public void Empty_buffer_has_no_ghost()
    {
        var s = Type("s", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Backspace), Completer, NoHistory);
        Assert.Equal("", s.Buffer);
        Assert.Equal("", s.Ghost);
    }

    [Fact]
    public void Tab_accepts_best_candidate_with_trailing_space()
    {
        var s = Type("pi", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("pick ", s.Buffer);
        Assert.Equal("", s.Ghost);
        Assert.Equal(0, s.CycleIndex);
        Assert.Equal("pi", s.TabPrefix);
    }

    [Fact]
    public void Tab_cycles_candidates_anchored_on_original_prefix()
    {
        // "re" matches remove and reserve (ordinal order)
        var s = Type("re", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("remove ", s.Buffer);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("reserve ", s.Buffer);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("remove ", s.Buffer);   // wraps
    }

    [Fact]
    public void Tab_on_empty_buffer_is_inert()
    {
        var (s, action) = LineEditorLogic.Step(EditState.Empty, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("", s.Buffer);
        Assert.Equal(EditAction.Continue, action);
    }

    [Fact]
    public void Edit_cancels_tab_cycle()
    {
        var s = Type("re", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        s = Type("x", s);
        Assert.Equal(-1, s.CycleIndex);
        Assert.Equal("", s.TabPrefix);
    }

    [Fact]
    public void No_completion_after_space()
    {
        var s = Type("scan --t", EditState.Empty);
        Assert.Equal("", s.Ghost);
        var (after, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal(s.Buffer, after.Buffer);   // v1 does not complete arguments
    }

    [Fact]
    public void History_up_recalls_and_down_restores_live_buffer()
    {
        var history = new[] { "scan", "status" };   // index 0 = newest
        var s = Type("pi", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.UpArrow), Completer, history);
        Assert.Equal("scan", s.Buffer);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.UpArrow), Completer, history);
        Assert.Equal("status", s.Buffer);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.DownArrow), Completer, history);
        Assert.Equal("scan", s.Buffer);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.DownArrow), Completer, history);
        Assert.Equal("pi", s.Buffer);           // live buffer restored
        Assert.Equal("ck", s.Ghost);            // and its ghost recomputed
        Assert.Equal(-1, s.HistoryIndex);
    }

    [Fact]
    public void Up_at_oldest_entry_stays_put()
    {
        var history = new[] { "scan" };
        var s = EditState.Empty;
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.UpArrow), Completer, history);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.UpArrow), Completer, history);
        Assert.Equal("scan", s.Buffer);
        Assert.Equal(0, s.HistoryIndex);
    }

    [Fact]
    public void Enter_submits()
    {
        var s = Type("status", EditState.Empty);
        var (_, action) = LineEditorLogic.Step(s, Key(ConsoleKey.Enter), Completer, NoHistory);
        Assert.Equal(EditAction.Submit, action);
    }

    [Fact]
    public void Cursor_moves_do_not_cancel_cycle()
    {
        var s = Type("re", EditState.Empty);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Home), Completer, NoHistory);
        (s, _) = LineEditorLogic.Step(s, Key(ConsoleKey.Tab), Completer, NoHistory);
        Assert.Equal("reserve ", s.Buffer);   // cycle continued from remove -> reserve
    }
}
