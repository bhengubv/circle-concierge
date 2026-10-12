using System.Net;
using System.Net.Sockets;

namespace Concierge.Away;

/// <summary>Wire constants and IPv4 helpers for finding a local Concierge Desktop.</summary>
public static class AwayBeacon
{
    public const string MulticastGroup = "239.7.7.7";
    public const int Port = 47772;
    public const string Prefix = "concierge-away|";

    /// <summary>Calculate the local subnet's directed broadcast address.</summary>
    public static IPAddress DirectedBroadcastAddress(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Concierge LAN discovery requires an IPv4 address and mask.");

        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcast = new byte[addressBytes.Length];
        for (var i = 0; i < broadcast.Length; i++)
            broadcast[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        return new IPAddress(broadcast);
    }
}
