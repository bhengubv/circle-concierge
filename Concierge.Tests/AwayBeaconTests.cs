using System.Net;
using System.Net.Sockets;
using Concierge.Away;

namespace Concierge.Tests;

public sealed class AwayBeaconTests
{
    [Fact]
    public void Directed_broadcast_address_uses_the_interface_subnet_mask()
    {
        Assert.Equal(IPAddress.Parse("192.168.0.255"), AwayBeacon.DirectedBroadcastAddress(
            IPAddress.Parse("192.168.0.250"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal(IPAddress.Parse("10.42.31.255"), AwayBeacon.DirectedBroadcastAddress(
            IPAddress.Parse("10.42.16.25"), IPAddress.Parse("255.255.240.0")));
    }

    [Fact]
    public void Directed_broadcast_rejects_non_ipv4_inputs()
    {
        Assert.Throws<ArgumentException>(() => AwayBeacon.DirectedBroadcastAddress(
            IPAddress.IPv6Loopback, IPAddress.Parse("255.255.255.0")));
        Assert.Throws<ArgumentException>(() => AwayBeacon.DirectedBroadcastAddress(
            IPAddress.Parse("192.168.0.2"), IPAddress.IPv6Loopback));
    }
}
