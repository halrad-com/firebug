using System;
using System.Diagnostics;

namespace SsdpCore
{
    /// <summary>
    /// Library-wide logging control. Every SsdpCore class takes an optional
    /// <c>Action&lt;TraceLevel, string&gt;</c> sink; this switch decides which
    /// levels reach it, so callers turn library chatter up or down without
    /// writing filter logic into their callback:
    ///
    /// <code>
    ///   SsdpTrace.Switch.Level = TraceLevel.Warning;  // quiet: warnings + errors
    ///   SsdpTrace.Switch.Level = TraceLevel.Off;      // silent (except hard errors)
    ///   SsdpTrace.Switch.Level = TraceLevel.Verbose;  // everything (the default)
    /// </code>
    ///
    /// Also configurable without code via app.config (TraceSwitch name "SsdpCore").
    ///
    /// HARD ERRORS ALWAYS LOG: <see cref="TraceLevel.Error"/> bypasses the
    /// switch — the only way to silence errors is to pass no sink at all.
    ///
    /// The default is Verbose (full pass-through) so consumers that predate the
    /// switch keep the exact stream they always had.
    /// </summary>
    public static class SsdpTrace
    {
        /// <summary>The library's trace switch. Set <c>Switch.Level</c> to control output.</summary>
        public static readonly TraceSwitch Switch =
            new TraceSwitch("SsdpCore", "SsdpCore discovery library tracing", "Verbose");

        /// <summary>
        /// Wrap a caller-provided sink with the level filter. Null-safe: a null
        /// sink becomes a no-op. Used by every SsdpCore constructor.
        /// </summary>
        internal static Action<TraceLevel, string> Wrap(Action<TraceLevel, string> sink)
        {
            if (sink == null) return (_, __) => { };
            return (level, message) =>
            {
                if (level == TraceLevel.Error ||                       // hard errors always log
                    (level != TraceLevel.Off && Switch.Level >= level))
                {
                    sink(level, message);
                }
            };
        }
    }
}
