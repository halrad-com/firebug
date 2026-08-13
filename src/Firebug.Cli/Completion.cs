#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Firebug.Cli
{
    /// <summary>
    /// Completion contract for the interactive prompt: given the full current
    /// input line, return ordered candidate completions of the token being
    /// edited (best first), each as the WHOLE resulting line. Empty = no
    /// suggestion. Hint supplies non-acceptable guidance (rendered dim like a
    /// ghost, but Tab ignores it — Tab consults Complete, which is empty
    /// whenever only a hint shows).
    ///
    /// This file, LineEditor.cs and CommandCatalog.cs are a worked example of
    /// the console patterns from the Huddle orchestrator (halrad-com/huddle).
    /// Hint is a regular member rather than a default interface method because
    /// net48 cannot compile default interface implementations.
    /// </summary>
    public interface ICompleter
    {
        IReadOnlyList<string> Complete(string input);
        string Hint(string input);
    }

    /// <summary>
    /// The CLI's completable verb list, derived from <see cref="CommandCatalog"/>
    /// (every Program.Run switch case) plus 'quit' — a REPL-level intercept,
    /// not a Run case. 'exit' is an accepted-but-uncompleted alias of quit,
    /// same convention as the huddle reference's alias exclusions. Guarded
    /// against drift by VerbCompleterTests' catalog pin and by
    /// CommandCatalogTests' bidirectional parser parity checks.
    /// </summary>
    public static class FirebugVerbs
    {
        public static readonly string[] Names =
            CommandCatalog.Commands.Select(c => c.Name)
                .Concat(new[] { "quit" })
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
    }

    /// <summary>
    /// First-token prefix completer. Once a space is present, an argument is
    /// being typed — that layer belongs to <see cref="FirebugArgCompleter"/>.
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

        public string Hint(string input) => "";
    }

    /// <summary>
    /// Argument-aware completer for the interactive prompt: first token
    /// completes verbs, later tokens complete the current verb's flags from
    /// the catalog, and Hint shows the verb's flag grammar the moment the
    /// operator enters argument territory with nothing typed yet.
    /// </summary>
    public sealed class FirebugArgCompleter : ICompleter
    {
        private readonly VerbCompleter _verbs = new VerbCompleter();

        public IReadOnlyList<string> Complete(string input)
        {
            // Best-effort UI riding the keystroke path — a surprise here must
            // cost the operator a ghost, never the console (same rationale as
            // the huddle reference's catch-everything).
            try { return CompleteCore(input); }
            catch { return Array.Empty<string>(); }
        }

        private IReadOnlyList<string> CompleteCore(string input)
        {
            if (!input.Contains(" ")) return _verbs.Complete(input);

            var firstSpace = input.IndexOf(' ');
            var verb = input.Substring(0, firstSpace);
            var lastSpace = input.LastIndexOf(' ');
            var prefix = input.Substring(lastSpace + 1);    // token being edited; "" right after a space
            var baseLine = input.Substring(0, lastSpace + 1);

            if (!prefix.StartsWith("-", StringComparison.Ordinal))
                return Array.Empty<string>();               // flag values / positionals: not completed (v1)

            var cmd = CommandCatalog.Find(verb);
            if (cmd == null) return Array.Empty<string>();

            return cmd.Flags
                .SelectMany(f => f.Short == null ? new[] { f.Long } : new[] { f.Long, f.Short })
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => baseLine + n)
                .ToArray();
        }

        /// <summary>
        /// Grammar hint: exactly "verb" + trailing whitespace, nothing typed
        /// yet, gets the verb's flag grammar from the catalog. Anything else:
        /// no hint (completion, when available, is more actionable).
        /// </summary>
        public string Hint(string input)
        {
            try
            {
                var trimmed = input.TrimEnd(' ');
                if (trimmed.Length == 0 || trimmed.Contains(" ") || input.Length == trimmed.Length)
                    return "";
                var cmd = CommandCatalog.Find(trimmed);
                if (cmd == null || cmd.Flags.Length == 0) return "";
                return string.Join(" ", cmd.Flags.Select(
                    f => f.TakesValue ? $"[{f.Long} <v>]" : $"[{f.Long}]"));
            }
            catch { return ""; }
        }
    }
}
