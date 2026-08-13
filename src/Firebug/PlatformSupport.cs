#if NET5_0_OR_GREATER
// Firebug drives Windows Firewall, URL ACLs and UAC — Windows-only by nature.
// Saying so at the assembly level makes the net8.0 target honest in consumers'
// IntelliSense and silences the CA1416 platform-compatibility advisories that
// otherwise fire on every WindowsIdentity/WindowsPrincipal call site.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
