using System.Linq;
using Firebug;

namespace Firebug.Tests;

// The parse is pure over netsh's text, so it is testable without http.sys, without
// elevation, and without whatever happens to be listening on the build agent.
// Fixture mirrors the real shape measured on a live box: request queue -> Processes
// -> URL groups, with the owning process appearing BEFORE the groups it owns.
public class PortOwnerTests
{
    private const string Dump = @"
Snapshot of HTTP service state (Request Queue View):
-----------------------------------------------------

Request queue name: Request queue is unnamed.
    Version: 2.0
    State: Active
    Number of active processes attached: 1
    Processes:
        ID: 21404, image: C:\test\MB3\MusicBee.exe
    URL groups:
    URL group ID: FE00000A20000001
        State: Active
        Request queue name: Request queue is unnamed.
        Properties:
            Number of registered URLs: 1
            Registered URLs:
                HTTP://+:8080/
        Server session ID: FF00000A10000011

Request queue name: Request queue is unnamed.
    Version: 2.0
    State: Active
    Processes:
        ID: 15200, image: C:\test\MB3\Plugins\MBXHub.exe
    URL groups:
    URL group ID: FC00000A20000001
        State: Active
        Request queue name: Request queue is unnamed.
        Properties:
            Number of registered URLs: 3
            Registered URLs:
                HTTP://+:8081/META/APP/
                HTTP://+:8081/META/NOTIFY/
                HTTP://+:8081/META/SMTC/

Request queue name: Request queue is unnamed.
    Processes:
        ID: 15200, image: C:\test\MB3\Plugins\MBXHub.exe
    URL groups:
    URL group ID: FA00000D20000001
        Request queue name: Request queue is unnamed.
        Properties:
            Number of registered URLs: 1
            Registered URLs:
                HTTP://127.0.0.1:8081:127.0.0.1/OVERLAY/

Request queue name: Request queue is unnamed.
    Processes:
        ID: 4144, image: <?>
        Services: DSAService
    URL groups:
    URL group ID: FE00000D20000001
        Properties:
            Number of registered URLs: 2
            Registered URLs:
                HTTP://127.0.0.1:28390:127.0.0.1/
                HTTP://[::1]:28391/
";

    [Fact]
    public void Parses_every_registration()
        => Assert.Equal(7, PortOwner.Parse(Dump).Count);

    [Fact]
    public void Wildcard_registration_names_the_owning_process()
    {
        var r = Assert.Single(PortOwner.Parse(Dump).Where(x => x.Port == 8080));
        Assert.Equal(21404, r.Pid);
        Assert.Equal(@"C:\test\MB3\MusicBee.exe", r.Image);
    }

    // The trap: an explicit-IP registration is host:port:ip, so it carries TWO
    // colons. "the port is after the last colon" reads this as 127, and the bug
    // ships green because the wildcard form has only one colon.
    [Fact]
    public void Explicit_ip_registration_reads_the_port_not_the_trailing_ip()
    {
        var overlay = Assert.Single(PortOwner.Parse(Dump).Where(x => x.Url.IndexOf("OVERLAY", System.StringComparison.OrdinalIgnoreCase) >= 0));
        Assert.Equal(8081, overlay.Port);
    }

    // IPv6 literals are full of colons; the bracket is what delimits the host.
    [Fact]
    public void IPv6_literal_registration_reads_its_port()
        => Assert.Single(PortOwner.Parse(Dump).Where(x => x.Port == 28391));

    // One process can hold several request queues - the Shell holds its META
    // surfaces and the overlay bridge separately - so a parser must accumulate
    // across queues instead of stopping at the first hit.
    [Fact]
    public void One_process_can_own_several_queues()
    {
        var mine = PortOwner.Parse(Dump).Where(x => x.Pid == 15200).ToList();
        Assert.Equal(4, mine.Count);
        Assert.Contains(mine, x => x.Url.EndsWith("/META/SMTC/", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mine, x => x.Url.IndexOf("OVERLAY", System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // http.sys does not always resolve an image name. The PID is still evidence;
    // reporting "unknown owner" beats reporting the wrong one.
    [Fact]
    public void Unresolved_image_keeps_the_pid_and_nulls_the_name()
    {
        var r = Assert.Single(PortOwner.Parse(Dump).Where(x => x.Port == 28390));
        Assert.Equal(4144, r.Pid);
        Assert.Null(r.Image);
    }

    [Fact]
    public void ForPort_filters_to_that_port()
    {
        var rows = PortOwner.ForPort(Dump, 8081);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(15200, r.Pid));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("total gibberish, no registrations here")]
    public void Junk_input_yields_nothing_and_does_not_throw(string input)
        => Assert.Empty(PortOwner.Parse(input));
}
