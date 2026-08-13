using Firebug.Cli;

namespace Firebug.Tests;

/// <summary>
/// Pins the single-row viewport math behind the flicker-free renderer: nothing
/// may ever paint past width-2 (wrapping desyncs the row anchor), long input
/// horizontally scrolls, and the caret always lands on the row.
/// </summary>
public class LineViewportTests
{
    [Fact]
    public void Short_line_shows_everything_and_caret_follows_cursor()
    {
        var v = LineViewport.Compute(promptLen: 9, width: 120, bufferLen: 5, ghostLen: 3, cursor: 5);
        Assert.Equal(0, v.Start);
        Assert.Equal(5, v.Take);
        Assert.Equal(3, v.GhostTake);
        Assert.Equal(14, v.CaretCol);
    }

    [Fact]
    public void Long_buffer_scrolls_window_to_keep_caret_visible()
    {
        // row = 39 usable cells, 30 after the prompt; cursor at the end of a
        // 50-char buffer slides the window so the caret sits at the row edge.
        var v = LineViewport.Compute(promptLen: 9, width: 40, bufferLen: 50, ghostLen: 0, cursor: 50);
        Assert.Equal(20, v.Start);
        Assert.Equal(30, v.Take);
        Assert.Equal(0, v.GhostTake);
        Assert.Equal(39, v.CaretCol);   // never past width-1
    }

    [Fact]
    public void Ghost_is_truncated_to_remaining_cells()
    {
        var v = LineViewport.Compute(promptLen: 2, width: 12, bufferLen: 4, ghostLen: 10, cursor: 4);
        Assert.Equal(4, v.Take);
        Assert.Equal(5, v.GhostTake);   // 9 cells after prompt, 4 spent on buffer
        Assert.Equal(6, v.CaretCol);
    }

    [Fact]
    public void Cursor_inside_window_does_not_scroll()
    {
        var v = LineViewport.Compute(promptLen: 9, width: 40, bufferLen: 20, ghostLen: 0, cursor: 10);
        Assert.Equal(0, v.Start);
        Assert.Equal(20, v.Take);
        Assert.Equal(19, v.CaretCol);
    }

    [Theory]
    [InlineData(9, 5, 50, 10, 50)]   // prompt wider than the window
    [InlineData(0, 0, 10, 5, 3)]     // zero-width console
    [InlineData(4, 1, 0, 0, 0)]      // empty buffer, tiny window
    public void Degenerate_windows_never_go_negative_or_past_the_row(
        int promptLen, int width, int bufferLen, int ghostLen, int cursor)
    {
        var v = LineViewport.Compute(promptLen, width, bufferLen, ghostLen, cursor);
        Assert.True(v.Start >= 0);
        Assert.True(v.Take >= 0);
        Assert.True(v.GhostTake >= 0);
        Assert.True(v.Start + v.Take <= bufferLen);
        Assert.InRange(v.CaretCol, 0, Math.Max(0, width - 1));
    }
}
