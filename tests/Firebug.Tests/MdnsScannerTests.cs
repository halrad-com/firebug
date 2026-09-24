using SsdpCore;

namespace Firebug.Tests;

/// <summary>
/// The wire bytes <see cref="MdnsScanner"/> sends, and the match rule it applies to what
/// comes back. Both are pure; neither opens a socket.
///
/// <para><b>Why the query bytes are pinned to the bit.</b> With the unicast-response bit
/// set (QCLASS 0x8001) Google Cast receivers answer unicast to port 5353, and on Windows a
/// unicast datagram to that port is delivered to the DNS Client service, which bound it
/// first — never to the scanner's socket. Measured 2026-09-24 on one box with the scanner's
/// own bind recipe: 0 of 4 Cast receivers with the bit, 4 of 4 without. Devialet and HTTP
/// responders answered multicast regardless, which is why the bit sat there unnoticed.</para>
/// </summary>
public class MdnsScannerTests
{
    [Fact]
    public void PtrQuery_AsksForAMulticastReply_TheUnicastResponseBitIsClear()
    {
        var q = MdnsScanner.BuildPtrQuery("_googlecast._tcp.local");

        // The question is the last four bytes: QTYPE, QCLASS.
        var qtype = (q[q.Length - 4] << 8) | q[q.Length - 3];
        var qclass = (q[q.Length - 2] << 8) | q[q.Length - 1];

        Assert.Equal(12, qtype);                       // PTR
        Assert.Equal(0x0001, qclass);                  // IN, and nothing else
        Assert.Equal(0, q[q.Length - 2] & 0x80);       // the unicast-response bit, by name
    }

    [Fact]
    public void PtrQuery_HeaderIsAStandardQueryWithOneQuestion()
    {
        var q = MdnsScanner.BuildPtrQuery("_googlecast._tcp.local");

        Assert.Equal(0, (q[0] << 8) | q[1]);           // ID 0, the mDNS convention
        Assert.Equal(0, (q[2] << 8) | q[3]);           // flags: standard query
        Assert.Equal(1, (q[4] << 8) | q[5]);           // QDCOUNT
        Assert.Equal(0, (q[6] << 8) | q[7]);           // ANCOUNT
        Assert.Equal(0, (q[8] << 8) | q[9]);           // NSCOUNT
        Assert.Equal(0, (q[10] << 8) | q[11]);         // ARCOUNT
    }

    [Theory]
    [InlineData("_googlecast._tcp.local")]
    [InlineData("_googlecast._tcp.local.")]
    [InlineData("_googlecast._tcp")]
    public void PtrQuery_NameIsLengthPrefixedLabelsEndingInLocal(string serviceType)
    {
        var q = MdnsScanner.BuildPtrQuery(serviceType);

        var expected = new List<byte>();
        foreach (var label in new[] { "_googlecast", "_tcp", "local" })
        {
            expected.Add((byte)label.Length);
            expected.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        expected.Add(0);

        var name = q.Skip(12).Take(q.Length - 12 - 4).ToArray();
        Assert.Equal(expected.ToArray(), name);
    }

    [Theory]
    [InlineData("S3-811102bce6c36c2428a2f953f89b69bc._googlecast._tcp.local", "_googlecast._tcp", true)]
    [InlineData("S3-811102bce6c36c2428a2f953f89b69bc._googlecast._tcp.local.", "_googlecast._tcp.local", true)]
    [InlineData("_googlecast._tcp.local", "_googlecast._tcp", true)]
    [InlineData("_printer._sub._http._tcp.local", "_http._tcp", true)]          // DNS-SD subtype, label boundary
    [InlineData("Phantom._devialet._tcp.local", "_googlecast._tcp", false)]
    [InlineData("_tcp.local", "_devialet._tcp.local", false)]                   // the old substring hole
    [InlineData("x_googlecast._tcp.local", "_googlecast._tcp", false)]         // suffix only on a label boundary
    [InlineData("", "_googlecast._tcp", false)]
    public void PtrMatchesType_IsAnchoredOnTheWholeType(string ptr, string query, bool expected)
    {
        Assert.Equal(expected, MdnsScanner.PtrMatchesType(ptr, query));
    }
}
