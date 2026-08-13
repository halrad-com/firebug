#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Firebug.Cli
{
    /// <summary>
    /// Completion contract for the interactive prompt: given the full current
    /// input line, return ordered candidate completions of the token being
    /// edited (best first). Empty = no suggestion.
    ///
    /// This file and LineEditor.cs are a worked example of the console
    /// line-editor pattern from the Huddle orchestrator (halrad-com/huddle):
    /// a pure, unit-tested state machine driving a deliberately dumb painter.
    /// </summary>
    public interface ICompleter
    {
        IReadOnlyList<string> Complete(string input);
    }

    /// <summary>
    /// The CLI's verb catalog — single source of truth for completion. Keep in
    /// sync with the switch in Program.Run and the ShowUsage text.
    /// </summary>
    public static class FirebugVerbs
    {
        public static readonly string[] Names =
        {
            "add", "check", "help", "open", "pick", "quit",
            "remove", "reserve", "scan", "status",
        };
    }

    /// <summary>
    /// First-token prefix completer. Once a space is present, an argument is
    /// being typed and v1 does not complete arguments.
    /// </summary>
    public sealed class VerbCompleter : ICompleter
    {
        private readonly string[] _names;

        public VerbCompleter(IEnumerable<string>? names = null)
        {
            _names = (names ?? FirebugVerbs.Names)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<string> Complete(string input)
        {
            if (input.Contains(" ")) return Array.Empty<string>();
            return _names.Where(n => n.StartsWith(input, StringComparison.Ordinal)).ToArray();
        }
    }
}
