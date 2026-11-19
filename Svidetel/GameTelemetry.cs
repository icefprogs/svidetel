using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Svidetel;

internal sealed partial class GameTelemetry
{
    internal sealed record Sample(long? Latency, int? Cpu);
    internal sealed record Snapshot(string Source, bool IsMeasuringNetwork, Sample[] History)
    {
        internal long? Median
        {
            get
            {
                var values = History.Select(x => x.Latency).OfType<long>().Order().ToArray();
                if (values.Length == 0) return null;
                return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
            }
        }

        internal int Missing => History.Count(x => !x.Latency.HasValue);
        internal double? Jitter
        {
            get
            {
                var differences = History.Zip(History.Skip(1))
                    .Where(pair => pair.First.Latency.HasValue && pair.Second.Latency.HasValue)
                    .Select(pair => Math.Abs(pair.First.Latency!.Value - pair.Second.Latency!.Value)).ToArray();
                return differences.Length == 0 ? null : differences.Average();
            }
        }
    }

    private readonly Queue<Sample> history = new();
    private (ulong Idle, ulong Total)? previousCpu;
    private (IPAddress Address, string Name)? gateway;
    private int routeTurns;

    internal async Task<Snapshot> Measure(CancellationToken token)
    {
        if (routeTurns++ % 8 == 0)
        {
            var current = DefaultGateway();
            if (!Equals(current, gateway)) history.Clear();
            gateway = current;
        }

        var cpu = ReadCpu();
        long? latency = null;
        if (gateway is { } target)
        {
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(target.Address, TimeSpan.FromMilliseconds(700), [], null, token);
                if (reply.Status == IPStatus.Success) latency = reply.RoundtripTime;
            }
            catch (PingException) { }
        }
        history.Enqueue(new(latency, cpu));
        if (history.Count > 24) history.Dequeue();
        return new(gateway is { } route ? $"{route.Name} · {route.Address}" : "Шлюз IPv4 не найден",
            gateway.HasValue, history.ToArray());
    }

    private int? ReadCpu()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user) == 0) return null;
        var total = kernel + user;
        var previous = previousCpu;
        previousCpu = (idle, total);
        if (previous is not { } old || total <= old.Total || idle < old.Idle) return null;
        return (int)Math.Clamp(100.0 * (1.0 - (double)(idle - old.Idle) / (total - old.Total)), 0, 100);
    }

    private static (IPAddress Address, string Name)? DefaultGateway()
    {
        try
        {
            // This only queries the Windows IPv4 routing table. ICMP is sent to the gateway.
            if (GetBestInterface(0x01010101, out var index) != 0) return null;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.Supports(NetworkInterfaceComponent.IPv4))
                    continue;
                try
                {
                    var properties = adapter.GetIPProperties();
                    if (properties.GetIPv4Properties()?.Index != index) continue;
                    var gateway = properties.GatewayAddresses.Select(g => g.Address)
                        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any) && !IPAddress.IsLoopback(ip));
                    if (gateway is not null) return (gateway, adapter.Name);
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destination, out uint index);

    [LibraryImport("kernel32.dll")]
    private static partial int GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
