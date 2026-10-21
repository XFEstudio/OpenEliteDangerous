using System.Globalization;
using System.Net;

namespace XFEEliteDangerous.NativeFormat.Replication;

/// <summary>
/// The verified IP4 branch of the native connectionDetails grammar.
/// This describes an endpoint; it does not implement its UDP session protocol.
/// </summary>
public readonly record struct EdServerIp4Endpoint
{
    public uint Address { get; }
    public ushort Port { get; }
    public ushort Mtu { get; }

    public EdServerIp4Endpoint(uint address, ushort port, ushort mtu) =>
        (Address, Port, Mtu) = (address, port, mtu);

    public IPAddress IPAddress => new(new byte[]
    {
        (byte)(Address >> 24), (byte)(Address >> 16), (byte)(Address >> 8), (byte)Address
    });

    public IPEndPoint ToIPEndPoint() => new(IPAddress, Port);

    /// <summary>
    /// Parses IP4:a.b.c.d:port,mtu. Native parsing is permissive; this implementation
    /// rejects truncation, overflow and trailing input instead of silently wrapping.
    /// Other native schemes require separate, verified parsers.
    /// </summary>
    public static EdServerIp4Endpoint Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out var endpoint))
            throw new FormatException("Expected IP4:a.b.c.d:port,mtu with IPv4 octets and UInt16 port/MTU.");
        return endpoint;
    }

    public static bool TryParse(string? value, out EdServerIp4Endpoint endpoint)
    {
        endpoint = default;
        if (value is null || value.Length > 64 || !value.StartsWith("IP4:", StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> remaining = value.AsSpan(4);
        uint address = 0;
        for (var octet = 0; octet < 4; octet++)
        {
            if (!ReadNumber(ref remaining, octet == 3 ? ':' : '.', byte.MaxValue, out var number))
                return false;
            address = (address << 8) | number;
        }
        if (!ReadNumber(ref remaining, ',', ushort.MaxValue, out var port)
            || !ReadNumber(ref remaining, '\0', ushort.MaxValue, out var mtu))
            return false;
        endpoint = new(address, (ushort)port, (ushort)mtu);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"IP4:{(byte)(Address >> 24)}.{(byte)(Address >> 16)}.{(byte)(Address >> 8)}.{(byte)Address}:{Port},{Mtu}");

    private static bool ReadNumber(ref ReadOnlySpan<char> text, char delimiter, uint maximum, out uint number)
    {
        number = 0;
        var length = delimiter == '\0' ? text.Length : text.IndexOf(delimiter);
        if (length is < 1 or > 5)
            return false;
        foreach (var digit in text[..length])
        {
            if (digit is < '0' or > '9')
                return false;
            number = number * 10 + (uint)(digit - '0');
            if (number > maximum)
                return false;
        }
        text = delimiter == '\0' ? [] : text[(length + 1)..];
        return true;
    }
}
