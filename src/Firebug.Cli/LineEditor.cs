#nullable enable
using System;
using System.Collections.Generic;

#if !NET5_0_OR_GREATER
// init-accessor support for record structs on .NET Framework.
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
#endif

namespace Firebug.Cli
{
    /// <summary>
    /// Console line editor with Tab completion, ghost-text suggestion and
    /// history — ported from the Huddle orchestrator's console
    /// (halrad-com/huddle) as a worked example of the pattern: everything
    /// decision-shaped lives in the pure <see cref="LineEditorLogic.Step"/>
    /// state machine (unit-tested in Firebug.Tests), and the only untestable
    /// code is "read a key, paint a line".
    /// </summary>
    public enum EditAction { Continue, Submit, Cancel }

    public readonly record struct EditState(
        string Buffer, int Cursor, string Ghost, int HistoryIndex, int CycleIndex, string TabPrefix,
        // The live buffer parked while the operator walks history (HistoryIndex >= 0);
        // Down past the newest entry puts it back. Deliberately its own field rather
        // than sharing TabPrefix, so Tab-cycling and history nav cannot corrupt each other.
        string StashedBuffer)
    {
        public static EditState Empty { get; } = new("", 0, "", -1, -1, "", "");
    }

    public static class LineEditorLogic
    {
        // Ghost = remainder of the best completion of the *whole current buffer*
        // (v1 completes the first token only; VerbCompleter returns empty once a
        // space is present, so the ghost naturally disappears after the verb).
        private static string ComputeGhost(string buffer, ICompleter completer)
        {
            // An empty buffer has no ghost: Complete("") returns the whole verb list,
            // which would otherwise sprout a suggestion the moment the operator clears
            // the line.
            if (buffer.Length == 0) return "";
            var matches = completer.Complete(buffer);
            if (matches.Count == 0) return "";
            var top = matches[0];
            return top.Length > buffer.Length ? top.Substring(buffer.Length) : "";
        }

        private static EditState WithBuffer(EditState s, string buffer, int cursor, ICompleter completer)
            => s with { Buffer = buffer, Cursor = cursor, Ghost = ComputeGhost(buffer, completer),
                        CycleIndex = -1, TabPrefix = "" }; // any edit cancels an in-progress Tab cycle

        // Start from EditState.Empty — it is the only safe entry point, since
        // default(EditState) leaves Buffer/Ghost/TabPrefix/StashedBuffer null.
        // `history` is ordered most-recent-first: index 0 is the newest command,
        // and HistoryIndex == -1 means "not in history, showing the live buffer".
        public static (EditState, EditAction) Step(
            EditState s, ConsoleKeyInfo key, ICompleter completer, IReadOnlyList<string> history)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return (s, EditAction.Submit);

                case ConsoleKey.Backspace:
                    if (s.Cursor > 0)
                    {
                        var b = s.Buffer.Remove(s.Cursor - 1, 1);
                        return (WithBuffer(s, b, s.Cursor - 1, completer), EditAction.Continue);
                    }
                    return (s, EditAction.Continue);

                case ConsoleKey.Delete:
                    if (s.Cursor < s.Buffer.Length)
                    {
                        var b = s.Buffer.Remove(s.Cursor, 1);
                        return (WithBuffer(s, b, s.Cursor, completer), EditAction.Continue);
                    }
                    return (s, EditAction.Continue);

                case ConsoleKey.LeftArrow:
                    return (s with { Cursor = Math.Max(0, s.Cursor - 1) }, EditAction.Continue);

                case ConsoleKey.RightArrow:
                    return (s with { Cursor = Math.Min(s.Buffer.Length, s.Cursor + 1) }, EditAction.Continue);

                case ConsoleKey.Home:
                    return (s with { Cursor = 0 }, EditAction.Continue);

                case ConsoleKey.End:
                    return (s with { Cursor = s.Buffer.Length }, EditAction.Continue);

                // A cycle is anchored on TabPrefix — the buffer as it stood when Tab
                // was first pressed — so repeated Tab walks the same candidate list.
                // Only an edit (via WithBuffer) or history nav cancels it; cursor-only
                // moves deliberately leave the cycle intact. Accepting a candidate
                // appends a trailing space: the operator is done with the verb and
                // ready to type arguments (which also stops the ghost, since the
                // completer returns nothing once the line contains a space).
                case ConsoleKey.Tab:
                {
                    if (s.CycleIndex < 0)
                    {
                        // An empty prompt stays inert — otherwise Tab would type the
                        // alphabetically-first verb and open a cycle over the whole catalog.
                        if (s.Buffer.Length == 0) return (s, EditAction.Continue);
                        var cands = completer.Complete(s.Buffer);
                        if (cands.Count == 0) return (s, EditAction.Continue);
                        var accepted = cands[0] + " ";
                        return (s with { Buffer = accepted, Cursor = accepted.Length,
                                         Ghost = "", CycleIndex = 0, TabPrefix = s.Buffer },
                                EditAction.Continue);
                    }
                    else
                    {
                        var cands = completer.Complete(s.TabPrefix);
                        if (cands.Count == 0) return (s, EditAction.Continue);
                        var next = (s.CycleIndex + 1) % cands.Count;
                        var cycled = cands[next] + " ";
                        return (s with { Buffer = cycled, Cursor = cycled.Length,
                                         Ghost = "", CycleIndex = next },
                                EditAction.Continue);
                    }
                }

                case ConsoleKey.UpArrow:
                {
                    if (history.Count == 0) return (s, EditAction.Continue);
                    var idx = s.HistoryIndex;
                    var stash = idx < 0 ? s.Buffer : s.StashedBuffer; // entering history: stash live buffer
                    var newIdx = Math.Min(idx + 1, history.Count - 1);
                    var recalled = history[newIdx];
                    return (s with { Buffer = recalled, Cursor = recalled.Length, Ghost = "",
                                     HistoryIndex = newIdx, StashedBuffer = stash,
                                     CycleIndex = -1, TabPrefix = "" }, EditAction.Continue);
                }

                case ConsoleKey.DownArrow:
                {
                    if (s.HistoryIndex < 0) return (s, EditAction.Continue);
                    var newIdx = s.HistoryIndex - 1;
                    if (newIdx < 0)
                    {
                        // back to the live buffer
                        return (s with { Buffer = s.StashedBuffer, Cursor = s.StashedBuffer.Length,
                                         Ghost = ComputeGhost(s.StashedBuffer, completer),
                                         HistoryIndex = -1, StashedBuffer = "",
                                         CycleIndex = -1, TabPrefix = "" }, EditAction.Continue);
                    }
                    var recalled = history[newIdx];
                    return (s with { Buffer = recalled, Cursor = recalled.Length, Ghost = "",
                                     HistoryIndex = newIdx, CycleIndex = -1, TabPrefix = "" },
                            EditAction.Continue);
                }

                default:
                    if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                    {
                        var b = s.Buffer.Insert(s.Cursor, key.KeyChar.ToString());
                        return (WithBuffer(s, b, s.Cursor + 1, completer), EditAction.Continue);
                    }
                    return (s, EditAction.Continue);
            }
        }
    }

    /// <summary>
    /// The interactive half: owns the console (keys in, pixels out) and drives
    /// the pure state machine. Do not use when stdin is redirected —
    /// Console.ReadKey has no meaning there.
    /// </summary>
    public sealed class LineEditor
    {
        private readonly ICompleter _completer;
        private readonly int _cap;
        private readonly List<string> _history = new List<string>(); // index 0 = most recent, as Step expects

        public LineEditor(ICompleter completer, int historyCapacity = 200)
        {
            _completer = completer;
            _cap = historyCapacity;
        }

        /// <summary>Read one line interactively. Returns the submitted line.</summary>
        public string? ReadLine(string prompt)
        {
            var s = EditState.Empty;
            Render(prompt, s);

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var (next, action) = LineEditorLogic.Step(s, key, _completer, _history);

                if (action == EditAction.Submit)
                {
                    s = next;
                    // Repaint without the ghost first: the suggestion is not part of
                    // the committed line and must not be left on screen above output.
                    Render(prompt, s with { Ghost = "" });
                    Console.WriteLine();
                    Push(s.Buffer);
                    return s.Buffer;
                }

                if (action == EditAction.Cancel) { Console.WriteLine(); return null; }

                s = next;
                Render(prompt, s);
            }
        }

        private void Push(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _history.RemoveAll(h => h == line); // re-running a command promotes it, not duplicates it
            _history.Insert(0, line);
            while (_history.Count > _cap) _history.RemoveAt(_history.Count - 1);
        }

        // Always a full-line redraw. Tab and history nav replace the buffer
        // wholesale and can *shorten* it, so append-only drawing would leave
        // stale characters behind.
        private static void Render(string prompt, EditState s)
        {
            try
            {
                int row = Console.CursorTop;
                int width = Console.WindowWidth;

                Console.SetCursorPosition(0, row);
                // Width-1 spaces: writing the final column would wrap to the next row.
                Console.Write(new string(' ', Math.Max(0, width - 1)));
                Console.SetCursorPosition(0, row);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(prompt);
                Console.ResetColor();
                Console.Write(s.Buffer);

                if (s.Ghost.Length > 0)
                {
                    // The ghost belongs at the END of the buffer, not at the caret:
                    // it is only recomputed on edits, so on a cursor-only move a
                    // caret-anchored ghost would show a completion of text it no
                    // longer follows.
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(s.Ghost);
                    Console.ResetColor();
                }

                // Caret sits inside the buffer, ahead of any ghost text.
                var col = Math.Max(0, Math.Min(prompt.Length + s.Cursor, Math.Max(0, width - 1)));
                Console.SetCursorPosition(col, row);
            }
            catch (System.IO.IOException) { /* not a real console (redirected) */ }
            catch (ArgumentOutOfRangeException) { /* window too small / resized mid-draw */ }
            finally { Console.ResetColor(); }
        }
    }
}
