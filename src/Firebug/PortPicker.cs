using System;
using System.Net;
using System.Net.Sockets;

namespace Firebug
{
    /// <summary>
    /// Picks a free TCP port for a local HTTP/media server, and probes whether a
    /// given port can be bound right now. The companion to <see cref="FirebugManager"/>:
    /// PortPicker chooses the port, FirebugManager authorizes it (URL ACL + firewall).
    ///
    /// Why this matters: many network media receivers are HANDED the full media URL
    /// (host + port) by the sender and simply fetch it — they never discover or guess
    /// the port. So a server's listener port is its own business, and any fixed value
    /// eventually collides. Prefer: pick a free port, PERSIST it (so the URL ACL and
    /// firewall reservation stay aligned across restarts), and only re-pick when the
    /// saved port is actually busy.
    /// </summary>
    public static class PortPicker
    {
        /// <summary>
        /// True if a TcpListener can bind this port right now. Availability only —
        /// this is independent of Windows URL ACLs and admin rights (a separate
        /// question answered by <see cref="FirebugManager.HasUrlAcl"/>).
        /// </summary>
        public static bool IsFree(int port)
        {
            TcpListener l = null;
            try
            {
                l = new TcpListener(IPAddress.Any, port);
                l.Start();
                return true;
            }
            catch (SocketException) { return false; }
            catch { return false; }
            finally { try { l?.Stop(); } catch { } }
        }

        /// <summary>
        /// Start at <paramref name="preferred"/> (default 8000) and step up until a
        /// free port is found. Returns <paramref name="preferred"/> if none of the
        /// candidates are free, so the caller's bind fails loudly rather than
        /// silently serving on an unexpected port.
        /// </summary>
        public static int Pick(int preferred = 8000, int tries = 20, Action<string> log = null)
        {
            for (int p = preferred; p < preferred + tries; p++)
            {
                if (IsFree(p))
                {
                    log?.Invoke($"port: selected {p}");
                    return p;
                }
                log?.Invoke($"port: {p} busy, trying next");
            }
            log?.Invoke($"port: none free in [{preferred}..{preferred + tries - 1}], using {preferred} anyway");
            return preferred;
        }

        /// <summary>
        /// Low port of the first ADJACENT pair (<c>p</c>, <c>p+1</c>) where both are
        /// free, starting at <paramref name="preferred"/> and stepping in twos. For
        /// servers that need a main port plus a side-channel on <c>port + 1</c>.
        /// Returns <paramref name="preferred"/> if no free pair is found.
        /// </summary>
        public static int PickPair(int preferred = 8000, int steps = 10, Action<string> log = null)
        {
            for (int i = 0; i < steps; i++)
            {
                int low = preferred + i * 2;
                if (IsFree(low) && IsFree(low + 1))
                {
                    log?.Invoke($"port pair: selected {low}/{low + 1}");
                    return low;
                }
                log?.Invoke($"port pair: {low}/{low + 1} unavailable, trying next");
            }
            log?.Invoke($"port pair: none free, falling back to {preferred}");
            return preferred;
        }

        /// <summary>
        /// Resolve the port to use for this run: honor a previously saved port if it
        /// is still bindable, otherwise pick a fresh one. PERSIST the returned value
        /// so it stays stable next launch.
        /// </summary>
        /// <param name="savedPort">Port stored in settings, or 0/negative if none.</param>
        /// <param name="preferred">Preferred starting port when picking fresh.</param>
        public static int Resolve(int savedPort, int preferred = 8000, Action<string> log = null)
        {
            if (savedPort > 0 && IsFree(savedPort))
            {
                log?.Invoke($"port: reusing saved {savedPort}");
                return savedPort;
            }
            if (savedPort > 0)
                log?.Invoke($"port: saved {savedPort} busy, picking a new one");
            return Pick(preferred, log: log);
        }
    }
}
