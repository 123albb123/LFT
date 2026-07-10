using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanFileTransfer.App.Models;

namespace LanFileTransfer.App.Services;

public sealed class NetworkAddressService
{
    public IReadOnlyList<IPAddress> GetDisplayAddresses()
    {
        return GetPrivateUnicastNetworks()
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .OrderBy(AddressRank)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public bool IsLocalNetworkClient(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }
        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        if (!IsPrivateOrLinkLocal(remoteAddress))
        {
            return false;
        }

        return GetPrivateUnicastNetworks().Any(network =>
            network.Address.AddressFamily == remoteAddress.AddressFamily &&
            IsInSameSubnet(network.Address, remoteAddress, network.PrefixLength));
    }

    public IPAddress ResolveBindingAddress(BindingMode mode, string? configuredAddress)
    {
        var addresses = GetDisplayAddresses();
        if (mode == BindingMode.Specific)
        {
            if (!IPAddress.TryParse(configuredAddress, out var requested) || !addresses.Contains(requested))
            {
                throw new InvalidOperationException("已保存的绑定 IP 不可用，请重新选择网卡或使用自动推荐。");
            }
            return requested;
        }

        return addresses.FirstOrDefault() ?? IPAddress.Loopback;
    }

    internal static bool IsInSameSubnet(IPAddress left, IPAddress right, int prefixLength)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        if (leftBytes.Length != rightBytes.Length || prefixLength < 0 || prefixLength > leftBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (leftBytes[index] != rightBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (leftBytes[fullBytes] & mask) == (rightBytes[fullBytes] & mask);
    }

    internal static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private static IReadOnlyList<NetworkPrefix> GetPrivateUnicastNetworks()
    {
        var result = new List<NetworkPrefix>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address.IsIPv4MappedToIPv6
                        ? unicast.Address.MapToIPv4()
                        : unicast.Address;
                    if (IsPrivateOrLinkLocal(address))
                    {
                        result.Add(new NetworkPrefix(address, unicast.PrefixLength));
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // 某些虚拟网卡会在枚举期间消失。
            }
        }
        return result;
    }

    private static int AddressRank(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168) return 0;
        if (bytes[0] == 10) return 1;
        if (bytes[0] == 172) return 2;
        return 3;
    }

    private sealed record NetworkPrefix(IPAddress Address, int PrefixLength);
}
