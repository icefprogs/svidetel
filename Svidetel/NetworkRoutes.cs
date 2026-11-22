using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Svidetel;

internal sealed record ActiveNetwork(NetworkInterface Adapter, IPAddress? Router, IPAddress Reference, IPAddress Alternate,
    string Key, string[] Addresses, string[] Dns, string? RouterAdapter);

internal sealed record NetworkConnection(string Key, string AdapterId, string Adapter, NetworkInterfaceType Type,
    long LinkSpeed, IPAddress? Router, IPAddress? Target, bool Automatic, string[] Addresses, string[] Dns, string? RouterAdapter);

internal static partial class NetworkRoutes
{
    private static readonly IPAddress ReferenceV4 = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress AlternateV4 = IPAddress.Parse("1.0.0.1");
    private static readonly IPAddress ReferenceV6 = IPAddress.Parse("2606:4700:4700::1111");
    private static readonly IPAddress AlternateV6 = IPAddress.Parse("2606:4700:4700::1001");

    internal static ActiveNetwork? Find(IPAddress? target = null)
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            if (target is not null && !IPAddress.IsLoopback(target) && FindRoute(target, adapters) is { } requested) return requested;
            return FindRoute(ReferenceV4, adapters) ?? FindRoute(ReferenceV6, adapters);
        }
        catch (NetworkInformationException) { return null; }
    }

    private static ActiveNetwork? FindRoute(IPAddress address, NetworkInterface[] adapters)
    {
        if (BestInterface(address) is not { } index) return null;
        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            try
            {
                var properties = adapter.GetIPProperties();
                int? adapterIndex = ipv6 ? properties.GetIPv6Properties()?.Index : properties.GetIPv4Properties()?.Index;
                if (adapterIndex != index) continue;
                var router = properties.GatewayAddresses.Select(item => item.Address).FirstOrDefault(ip =>
                    ip.AddressFamily == address.AddressFamily && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip));
                if (router is { IsIPv6LinkLocal: true, ScopeId: 0 })
                    router = new IPAddress(router.GetAddressBytes(), index);
                string? routerAdapter = null;
                if (router is null && !ipv6 && LocalIpv4Gateway(adapters) is { } local)
                {
                    router = local.Router;
                    routerAdapter = local.Adapter;
                }
                var addresses = properties.UnicastAddresses.Select(item => item.Address).Where(ip => !IPAddress.IsLoopback(ip)).ToArray();
                var selectedFamily = addresses.Where(ip => ip.AddressFamily == address.AddressFamily)
                    .Select(ip => ip.ToString()).Order(StringComparer.Ordinal);
                var dns = properties.DnsAddresses.Select(ip => ip.ToString()).ToArray();
                var key = $"{adapter.Id}|{routerAdapter}|{router}|{properties.DnsSuffix}|{string.Join(",", selectedFamily)}";
                return new(adapter, router, ipv6 ? ReferenceV6 : ReferenceV4, ipv6 ? AlternateV6 : AlternateV4,
                    key, addresses.Select(ip => ip.ToString()).ToArray(), dns, routerAdapter);
            }
            catch (NetworkInformationException) { }
        }
        return null;
    }

    private static uint? BestInterface(IPAddress address)
    {
        var destination = new IPEndPoint(address, 0).Serialize();
        return GetBestInterfaceEx(destination.Buffer.Span, out uint index) == 0 ? index : null;
    }

    private static unsafe (IPAddress Router, string Adapter)? LocalIpv4Gateway(NetworkInterface[] adapters)
    {
        uint size = 0;
        if (GetIpForwardTable(0, ref size, 0) != 122 || size is < 4 or > 4_194_304) return null;
        var table = new byte[size];
        fixed (byte* pointer = table)
        {
            if (GetIpForwardTable((nint)pointer, ref size, 0) != 0) return null;
        }
        var routes = new List<(IPAddress Gateway, uint Index, uint Metric)>();
        const int rowSize = 14 * sizeof(uint);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count && i < (table.Length - sizeof(uint)) / rowSize; i++)
        {
            var row = table.AsSpan(sizeof(uint) + i * rowSize, rowSize);
            if (Read(row, 0) != 0 || Read(row, 1) != 0 || Read(row, 3) == 0 ||
                Read(row, 5) is not (3 or 4)) continue;
            routes.Add((new IPAddress(row.Slice(3 * sizeof(uint), sizeof(uint))), Read(row, 4), Read(row, 9)));
        }
        foreach (var entry in routes.OrderBy(entry => entry.Metric))
        {
            if (BestInterface(entry.Gateway) != entry.Index) continue;
            foreach (var adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    if (adapter.GetIPProperties().GetIPv4Properties()?.Index == entry.Index)
                        return (entry.Gateway, adapter.Name);
                }
                catch (NetworkInformationException) { }
            }
        }
        return null;

        static uint Read(ReadOnlySpan<byte> row, int column) => BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(column * sizeof(uint)));
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterfaceEx(ReadOnlySpan<byte> destination, out uint index);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpForwardTable(nint table, ref uint size, int order);
}
