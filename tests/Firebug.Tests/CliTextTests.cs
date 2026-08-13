using System.Runtime.InteropServices;
using Firebug.Cli;

namespace Firebug.Tests;

/// <summary>
/// Pins the command-line text plumbing. QuoteArg guards a privilege boundary
/// (values flattened into an ELEVATED child's command line), so its gold test
/// is a genuine round-trip through Windows' own CommandLineToArgvW — the exact
/// parser the elevated child uses.
/// </summary>
public class CliTextTests
{
    // --- QuoteArg: round-trip through the real Windows parser ----------------

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] ParseLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(argv); }
    }

    public static TheoryData<string[]> HostileVectors => new()
    {
        new[] { "reserve", "--name", "MyApp", "--port", "8080" },
        new[] { "reserve", "--name", "My App", "--port", "8080" },
        // The C1 injection vector: a name that re-parsed as --pick in the child
        new[] { "reserve", "--name", "X\" --pick \"", "--port", "8080" },
        // Trailing backslash must not eat the closing quote
        new[] { "reserve", "--name", "My App\\", "--port", "8080" },
        // Backslash runs before an embedded quote
        new[] { "add", "--name", "a\\\\\"b", "--port", "1" },
        new[] { "add", "--name", "", "--port", "1" },          // empty argument survives
        new[] { "add", "--name", "tab\there", "--port", "1" }, // tab forces quoting
    };

    [Theory]
    [MemberData(nameof(HostileVectors))]
    public void QuoteArg_round_trips_through_CommandLineToArgvW(string[] original)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;   // parser is the Windows one

        // CommandLineToArgvW treats the first token as a program path with
        // different rules, so parse with a dummy exe prepended — exactly the
        // shape ProcessStartInfo produces.
        var flattened = "firebug.exe " + string.Join(" ", original.Select(CliText.QuoteArg));
        var parsed = ParseLikeWindows(flattened);
        Assert.Equal("firebug.exe", parsed[0]);
        Assert.Equal(original, parsed.Skip(1).ToArray());
    }

    [Fact]
    public void QuoteArg_leaves_plain_args_unquoted()
    {
        Assert.Equal("reserve", CliText.QuoteArg("reserve"));
        Assert.Equal("--port", CliText.QuoteArg("--port"));
        Assert.Equal("8080", CliText.QuoteArg("8080"));
    }

    // --- SplitArgs (REPL tokenization) ---------------------------------------

    [Fact]
    public void SplitArgs_splits_on_whitespace()
    {
        Assert.Equal(new[] { "add", "--name", "X" }, CliText.SplitArgs("add   --name\tX"));
    }

    [Fact]
    public void SplitArgs_honors_quotes()
    {
        Assert.Equal(new[] { "add", "--name", "My App" }, CliText.SplitArgs("add --name \"My App\""));
    }

    [Fact]
    public void SplitArgs_preserves_empty_quoted_argument()
    {
        // A quoted empty string is a real argument — dropping it would make
        // --name silently swallow the next flag.
        Assert.Equal(new[] { "add", "--name", "" }, CliText.SplitArgs("add --name \"\""));
    }
}
