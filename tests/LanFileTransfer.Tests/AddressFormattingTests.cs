using System.Net;
using LanFileTransfer.App;

namespace LanFileTransfer.Tests;

public sealed class AddressFormattingTests
{
    [Fact]
    public void FormatsIpv4WithoutBrackets() => Assert.Equal("192.168.1.88", MainWindow.FormatHost(IPAddress.Parse("192.168.1.88")));

    [Theory]
    [InlineData("fd00::1234")]
    [InlineData("fe80::1234")]
    public void FormatsIpv6WithBrackets(string address) => Assert.Equal($"[{address}]", MainWindow.FormatHost(IPAddress.Parse(address)));
}
