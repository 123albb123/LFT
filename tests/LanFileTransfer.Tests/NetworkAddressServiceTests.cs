using System.Net;
using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class NetworkAddressServiceTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.88.12", true)]
    [InlineData("169.254.1.2", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void RecognizesPrivateAndLinkLocalRanges(string text, bool expected)
    {
        Assert.Equal(expected, NetworkAddressService.IsPrivateOrLinkLocal(IPAddress.Parse(text)));
    }

    [Theory]
    [InlineData("192.168.1.4", "192.168.1.220", 24, true)]
    [InlineData("192.168.1.4", "192.168.2.4", 24, false)]
    [InlineData("10.8.1.4", "10.8.200.9", 16, true)]
    public void ComparesSubnetPrefixes(string left, string right, int prefix, bool expected)
    {
        Assert.Equal(expected, NetworkAddressService.IsInSameSubnet(IPAddress.Parse(left), IPAddress.Parse(right), prefix));
    }

    [Fact]
    public void DoesNotTrustMissingRemoteAddress()
    {
        Assert.False(new NetworkAddressService().IsLocalNetworkClient(null));
    }

    [Theory]
    [InlineData("::1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    public void RecognizesLocalIpv6Ranges(string text, bool expected)
    {
        Assert.Equal(expected, NetworkAddressService.IsPrivateOrLinkLocal(IPAddress.Parse(text)));
    }

    [Theory]
    [InlineData("OpenVPN TAP", true)]
    [InlineData("Radmin VPN", true)]
    [InlineData("Wi-Fi", false)]
    public void ClassifiesVirtualKeywords(string text, bool expected)
    {
        var option = new NetworkAddressService.NetworkAddressOption(IPAddress.Parse("192.168.1.2"), 24, text, text, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, text.Contains("VPN", StringComparison.OrdinalIgnoreCase) || text.Contains("TAP", StringComparison.OrdinalIgnoreCase), text.Contains("VPN", StringComparison.OrdinalIgnoreCase) || text.Contains("TAP", StringComparison.OrdinalIgnoreCase), false);
        Assert.Equal(expected, option.IsVirtual || option.IsVpnLike);
    }
}
