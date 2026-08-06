using System.Net;
using System.Net.Sockets;

namespace FlexFetch.Services.Route;

/// <summary>
/// Matches IP addresses against a set of CIDR ranges in O(log N) time.
///
/// IPv4 and IPv6 ranges are normalized to inclusive (start, end) intervals
/// and stored in separate sorted arrays; matching binary-searches the start
/// and verifies the end. This follows the same approach as the PrismWay
/// reference routing implementation (sort, merge, binary search).
/// </summary>
public sealed class CidrMatcher
{
    private (uint Start, uint End)[] _v4 = Array.Empty<(uint, uint)>();
    private (ulong HStart, ulong LStart, ulong HEnd, ulong LEnd)[] _v6 = Array.Empty<(ulong, ulong, ulong, ulong)>();

    /// <summary>
    /// Atomically replaces the rule set. Invalid lines are skipped and
    /// reported through the optional diagnostic sink.
    /// </summary>
    public void Load(IEnumerable<string> cidrStrings, Action<string>? diagnosticSink = null)
    {
        var v4 = new List<(uint Start, uint End)>();
        var v6 = new List<(ulong HStart, ulong LStart, ulong HEnd, ulong LEnd)>();

        foreach (var raw in cidrStrings)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (line[0] == '#')
            {
                continue;
            }

            try
            {
                if (line.Contains(':'))
                {
                    v6.Add(ParseV6(line));
                }
                else
                {
                    v4.Add(ParseV4(line));
                }
            }
            catch (Exception ex)
            {
                diagnosticSink?.Invoke($"CIDR rule '{line}' rejected: {ex.Message}");
            }
        }

        _v4 = MergeV4(v4);
        _v6 = MergeV6(v6);
    }

    /// <summary>Returns true when the IP falls inside any configured CIDR.</summary>
    public bool IsMatch(IPAddress? ip)
    {
        if (ip is null)
        {
            return false;
        }

        // Treat IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) as IPv4.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var addr = ReadUInt32(ip);
            return IsMatchV4(addr);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var (high, low) = ReadUInt128(ip);
            return IsMatchV6(high, low);
        }

        return false;
    }

    /// <summary>Replaces this matcher's rules with another matcher's rules.</summary>
    public void ReplaceFrom(CidrMatcher other)
    {
        _v4 = other._v4;
        _v6 = other._v6;
    }

    private bool IsMatchV4(uint addr)
    {
        int left = 0, right = _v4.Length - 1;
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            var (start, end) = _v4[mid];
            if (addr < start)
            {
                right = mid - 1;
            }
            else if (addr > end)
            {
                left = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    private bool IsMatchV6(ulong high, ulong low)
    {
        int left = 0, right = _v6.Length - 1;
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            var (hStart, lStart, hEnd, lEnd) = _v6[mid];
            int cmpStart = Compare128(high, low, hStart, lStart);
            if (cmpStart < 0)
            {
                right = mid - 1;
            }
            else if (Compare128(high, low, hEnd, lEnd) > 0)
            {
                left = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    // --- Parsing ---

    private static (uint Start, uint End) ParseV4(string line)
    {
        var (ipPart, subnet) = SplitSubnet(line, 32);
        var addr = ReadUInt32(IPAddress.Parse(ipPart));
        if (subnet is < 0 or > 32)
        {
            throw new ArgumentException("Subnet out of range for IPv4");
        }

        uint mask = MaskV4(subnet);
        if ((addr & mask) != addr)
        {
            throw new ArgumentException("Host bits are not zero");
        }

        uint start = addr & mask;
        uint end = subnet == 0 ? uint.MaxValue : start | ~mask;
        return (start, end);
    }

    private static (ulong HStart, ulong LStart, ulong HEnd, ulong LEnd) ParseV6(string line)
    {
        var (ipPart, subnet) = SplitSubnet(line, 128);
        var (high, low) = ReadUInt128(IPAddress.Parse(ipPart));
        if (subnet is < 0 or > 128)
        {
            throw new ArgumentException("Subnet out of range for IPv6");
        }

        var (hMask, lMask) = MaskV6(subnet);
        if ((high & hMask) != high || (low & lMask) != low)
        {
            throw new ArgumentException("Host bits are not zero");
        }

        ulong hStart = high & hMask;
        ulong lStart = low & lMask;
        ulong hEnd = subnet == 0 ? ulong.MaxValue : hStart | ~hMask;
        ulong lEnd = subnet == 0 ? ulong.MaxValue : lStart | ~lMask;
        return (hStart, lStart, hEnd, lEnd);
    }

    private static (string IpPart, int Subnet) SplitSubnet(string line, int defaultSubnet)
    {
        var slash = line.IndexOf('/');
        if (slash < 0)
        {
            return (line, defaultSubnet);
        }

        var ipPart = line[..slash];
        var subnet = int.Parse(line[(slash + 1)..]);
        return (ipPart, subnet);
    }

    private static uint MaskV4(int subnet)
    {
        if (subnet <= 0)
        {
            return 0;
        }
        if (subnet >= 32)
        {
            return uint.MaxValue;
        }
        return uint.MaxValue << (32 - subnet);
    }

    private static (ulong HMask, ulong LMask) MaskV6(int subnet)
    {
        if (subnet <= 0)
        {
            return (0, 0);
        }
        if (subnet >= 128)
        {
            return (ulong.MaxValue, ulong.MaxValue);
        }
        if (subnet == 64)
        {
            return (ulong.MaxValue, 0);
        }
        if (subnet < 64)
        {
            return (ulong.MaxValue << (64 - subnet), 0);
        }
        return (ulong.MaxValue, ulong.MaxValue << (128 - subnet));
    }

    private static uint ReadUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static (ulong High, ulong Low) ReadUInt128(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        ulong high = 0, low = 0;
        for (var i = 0; i < 8; i++)
        {
            high = (high << 8) | bytes[i];
        }
        for (var i = 8; i < 16; i++)
        {
            low = (low << 8) | bytes[i];
        }
        return (high, low);
    }

    private static int Compare128(ulong aHigh, ulong aLow, ulong bHigh, ulong bLow)
    {
        var cmp = aHigh.CompareTo(bHigh);
        return cmp != 0 ? cmp : aLow.CompareTo(bLow);
    }

    // --- Merge ---

    private static (uint, uint)[] MergeV4(List<(uint Start, uint End)> ranges)
    {
        if (ranges.Count == 0)
        {
            return Array.Empty<(uint, uint)>();
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(uint Start, uint End)> { ranges[0] };
        foreach (var current in ranges.Skip(1))
        {
            var last = merged[^1];
            if (current.Start <= last.End || current.Start == last.End + 1)
            {
                merged[^1] = (last.Start, Math.Max(last.End, current.End));
            }
            else
            {
                merged.Add(current);
            }
        }
        return merged.ToArray();
    }

    private static (ulong, ulong, ulong, ulong)[] MergeV6(List<(ulong HStart, ulong LStart, ulong HEnd, ulong LEnd)> ranges)
    {
        if (ranges.Count == 0)
        {
            return Array.Empty<(ulong, ulong, ulong, ulong)>();
        }

        ranges.Sort((a, b) => Compare128(a.HStart, a.LStart, b.HStart, b.LStart));
        var merged = new List<(ulong HStart, ulong LStart, ulong HEnd, ulong LEnd)> { ranges[0] };
        foreach (var current in ranges.Skip(1))
        {
            var last = merged[^1];
            var adjOrOverlap = Compare128(current.HStart, current.LStart, last.HEnd, last.LEnd) <= 0
                || IsAdjacent(current.HStart, current.LStart, last.HEnd, last.LEnd);
            if (adjOrOverlap)
            {
                var (hEnd, lEnd) = Compare128(current.HEnd, current.LEnd, last.HEnd, last.LEnd) > 0
                    ? (current.HEnd, current.LEnd)
                    : (last.HEnd, last.LEnd);
                merged[^1] = (last.HStart, last.LStart, hEnd, lEnd);
            }
            else
            {
                merged.Add(current);
            }
        }
        return merged.ToArray();
    }

    private static bool IsAdjacent(ulong hStart, ulong lStart, ulong hEnd, ulong lEnd)
    {
        // current.Start == last.End + 1 (128-bit arithmetic)
        if (lEnd == ulong.MaxValue)
        {
            return hStart == hEnd + 1 && lStart == 0;
        }
        return hStart == hEnd && lStart == lEnd + 1;
    }
}
