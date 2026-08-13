#nullable enable
using System.Collections.Generic;
using System.Text;

namespace Firebug.Cli
{
    /// <summary>
    /// Command-line text plumbing, split out so it is unit-testable (the CLI is
    /// a net48 exe; these are compiled-by-link into Firebug.Tests).
    ///
    /// QuoteArg exists because of a privilege boundary: RelaunchElevated
    /// flattens parsed values back into a command line for an ADMINISTRATOR
    /// child process. A naive space-only quoter lets a value containing quotes
    /// smuggle extra flags across the UAC boundary (e.g. a --name of
    /// 'X" --pick "' re-parsing as --pick in the elevated child).
    /// </summary>
    public static class CliText
    {
        /// <summary>
        /// Quote one argument per CommandLineToArgvW rules so the child parses
        /// exactly the value the parent held: backslash runs before a quote (or
        /// the closing quote) are doubled, embedded quotes become \".
        /// Values without whitespace or quotes pass through unquoted.
        /// </summary>
        public static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);   // escape the run AND the quote
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);               // a trailing run must not eat the closing quote
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Split a REPL line into args, honoring double quotes. A quoted empty
        /// string is a real (empty) argument, not nothing.
        /// </summary>
        public static string[] SplitArgs(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false, sawQuote = false;
            foreach (var c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; sawQuote = true; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0 || sawQuote) { result.Add(current.ToString()); current.Clear(); }
                    sawQuote = false;
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0 || sawQuote) result.Add(current.ToString());
            return result.ToArray();
        }
    }
}
