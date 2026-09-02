using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Firebug
{
    /// <summary>
    /// Who actually owns a port, for ports held through <c>http.sys</c>.
    ///
    /// <para>The TCP table cannot answer this. Every <c>HttpListener</c> binds through
    /// the kernel-mode HTTP driver, so <c>GetExtendedTcpTable</c> / <c>netstat</c>
    /// report the owner as <c>System (PID 4)</c> - true, and useless: "System" is not
    /// a program a user can be told to close. The owning application is known one layer
    /// up, to http.sys itself, which keeps a live table of request queues, the processes
    /// attached to them, and the URL prefixes each has registered.</para>
    ///
    /// <para>Read via <c>netsh http show servicestate view=requestq</c> rather than a
    /// P/Invoke: the HTTP Server API documented query surface covers URL ACL
    /// RESERVATIONS, a different table from live registrations, and shelling netsh is
    /// what this library already does for every ACL read. The parse is deliberately pure
    /// and anchored on <c>HTTP://</c> and <c>ID:</c> - netsh LABELS are localized on
    /// non-English Windows, its payloads are not.</para>
    ///
    /// <para>This is LIVE state: a process that is not running has registered nothing, so
    /// no rows means "not listening", never "not configured".</para>
    /// </summary>
    public static class PortOwner
    {
        /// <summary>One URL prefix registered with http.sys, and who registered it.</summary>
        public sealed class Registration
        {
            /// <summary>The prefix exactly as http.sys reports it, e.g. <c>HTTP://+:8080/</c>.</summary>
            public string Url { get; set; }

            /// <summary>Port taken from <see cref="Url"/>.</summary>
            public int Port { get; set; }

            /// <summary>Owning process id. Always present.</summary>
            public int Pid { get; set; }

            /// <summary>
            /// Owning image path, or null when http.sys reports its unresolved marker - it
            /// does not always resolve a name. The pid is still evidence; an unknown owner
            /// is a better answer than a guessed one.
            /// </summary>
            public string Image { get; set; }

            public override string ToString()
                => Image != null
                    ? Url + " -> " + Image + " (PID " + Pid + ")"
                    : Url + " -> PID " + Pid;
        }

        /// <summary>Live registrations for one port on this machine. Empty when nothing holds it.</summary>
        public static IReadOnlyList<Registration> ForPort(int port)
            => ForPort(RunNetsh("http show servicestate view=requestq"), port);

        /// <summary>Registrations for one port, from an already-captured dump. Pure.</summary>
        public static IReadOnlyList<Registration> ForPort(string serviceStateOutput, int port)
        {
            var hits = new List<Registration>();
            foreach (var r in Parse(serviceStateOutput))
                if (r.Port == port) hits.Add(r);
            return hits;
        }

        /// <summary>
        /// Every registration in a <c>netsh http show servicestate view=requestq</c> dump.
        /// Pure - no process is started, so this is testable against captured text.
        ///
        /// <para>Structure being walked: Request queue -&gt; Processes -&gt; URL groups -&gt;
        /// Registered URLs. The owning process appears BEFORE the groups it owns, and one
        /// process can hold several queues, so ownership is carried forward and reset at
        /// each queue rather than matched line-adjacently.</para>
        /// </summary>
        public static IReadOnlyList<Registration> Parse(string serviceStateOutput)
        {
            var results = new List<Registration>();
            if (string.IsNullOrEmpty(serviceStateOutput)) return results;

            int pid = 0;
            string image = null;
            bool haveProcess = false;
            bool inUrls = false;

            foreach (var raw in serviceStateOutput.Split('\n'))
            {
                var line = raw.Trim();

                // A queue header appears TWICE: once at column 0 opening the queue, and
                // again indented inside each URL group, which names its owning queue.
                // Only the column-0 one starts a new queue. Resetting on the nested one
                // discards the owner just read, and the whole dump parses to PID 0 -
                // which is what the live run did while a fixture without the nested line
                // stayed green.
                bool topLevel = raw.Length > 0 && !char.IsWhiteSpace(raw[0]);
                if (topLevel && line.StartsWith("Request queue name:", StringComparison.OrdinalIgnoreCase))
                {
                    // New queue: the previous queue owner does not carry into it.
                    pid = 0; image = null; haveProcess = false; inUrls = false;
                    continue;
                }

                if (line.StartsWith("Registered URLs:", StringComparison.OrdinalIgnoreCase))
                {
                    inUrls = true;
                    continue;
                }

                if (line.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
                {
                    // First process wins: a queue with several attached processes has no
                    // single owner, and choosing deterministically beats choosing last.
                    if (!haveProcess && TryParseProcess(line, out int p, out string img))
                    {
                        pid = p; image = img; haveProcess = true;
                    }
                    inUrls = false;
                    continue;
                }

                if (inUrls)
                {
                    if (line.StartsWith("HTTP://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParsePort(line, out int urlPort))
                        {
                            results.Add(new Registration
                            {
                                Url = line,
                                Port = urlPort,
                                Pid = pid,
                                Image = image
                            });
                        }
                        continue;
                    }
                    if (line.Length > 0) inUrls = false;   // list ended
                }
            }

            return results;
        }

        private static bool TryParseProcess(string line, out int pid, out string image)
        {
            pid = 0; image = null;
            int comma = line.IndexOf(',');
            string idPart = comma >= 0 ? line.Substring(3, comma - 3) : line.Substring(3);
            if (!int.TryParse(idPart.Trim(), out pid)) return false;

            if (comma >= 0)
            {
                int img = line.IndexOf("image:", comma, StringComparison.OrdinalIgnoreCase);
                if (img >= 0)
                {
                    var value = line.Substring(img + "image:".Length).Trim();
                    // http.sys prints an unresolved marker when it has no image name.
                    if (value.Length > 0 && value != "<?>") image = value;
                }
            }
            return true;
        }

        /// <summary>
        /// Port out of a registered prefix. Three forms exist and two are traps:
        /// <list type="bullet">
        /// <item><c>HTTP://+:8080/</c> - wildcard, one colon</item>
        /// <item><c>HTTP://127.0.0.1:8081:127.0.0.1/OVERLAY/</c> - explicit IP, which
        /// http.sys prints as host:port:ip, so "after the last colon" reads 127</item>
        /// <item><c>HTTP://[::1]:28391/</c> - an IPv6 literal is mostly colons; the
        /// bracket is what delimits the host</item>
        /// </list>
        /// </summary>
        private static bool TryParsePort(string url, out int port)
        {
            port = 0;
            const string scheme = "HTTP://";
            if (url.Length <= scheme.Length) return false;

            var rest = url.Substring(scheme.Length);
            int slash = rest.IndexOf('/');
            var authority = slash >= 0 ? rest.Substring(0, slash) : rest;
            if (authority.Length == 0) return false;

            if (authority[0] == '[')
            {
                int close = authority.IndexOf(']');
                if (close < 0 || close + 2 > authority.Length || authority[close + 1] != ':') return false;
                return TryLeadingInt(authority.Substring(close + 2), out port);
            }

            // host:port[:ip] - the port is the SECOND field, never the last.
            var parts = authority.Split(':');
            if (parts.Length < 2) return false;
            return TryLeadingInt(parts[1], out port);
        }

        private static bool TryLeadingInt(string s, out int value)
        {
            value = 0;
            int end = 0;
            while (end < s.Length && s[end] >= '0' && s[end] <= '9') end++;
            return end > 0 && int.TryParse(s.Substring(0, end), out value);
        }

        private static string RunNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    // Drain BEFORE waiting. A machine-wide servicestate dump is large, and
                    // WaitForExit-then-ReadToEnd deadlocks once the child fills the stdout
                    // pipe buffer - the same hazard already fixed in
                    // FirebugManager.RunNetshWithOutput.
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    return output;
                }
            }
            catch { return string.Empty; }
        }
    }
}
