using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Svidetel;

internal sealed partial class GameTelemetry(IPAddress? configuredAddress = null)
{
    internal sealed record Sample(long? Latency, int? Cpu, long? Router = null, bool RemoteProbed = false,
        string? Adapter = null, long? ReceiveKbps = null, long? SendKbps = null, bool RouterProbed = false);
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
    private (IPAddress Address, NetworkInterface Adapter)? gateway;
    private (long Received, long Sent, long Tick)? previousTraffic;
    private int routeTurns;

    internal async Task<Snapshot> Measure(CancellationToken token)
    {
        if (routeTurns++ % 8 == 0)
        {
            var current = DefaultGateway();
            if (!Equals(current?.Address, gateway?.Address) || current?.Adapter.Id != gateway?.Adapter.Id)
            {
                history.Clear();
                previousTraffic = null;
            }
            gateway = current;
        }

        var cpu = ReadCpu();
        var traffic = ReadTraffic(gateway?.Adapter);
        var routerTask = PingOnce(gateway?.Address, token);
        var remoteTask = configuredAddress is null || Equals(configuredAddress, gateway?.Address)
            ? routerTask : PingOnce(configuredAddress, token);
        await Task.WhenAll(routerTask, remoteTask);
        history.Enqueue(new(await remoteTask, cpu, await routerTask, configuredAddress is not null,
            gateway?.Adapter.Name, traffic?.Received, traffic?.Sent, gateway.HasValue));
        if (history.Count > 24) history.Dequeue();
        return new(configuredAddress is not null ? $"Указанный IP · {configuredAddress}"
                : gateway is { } route ? $"{route.Adapter.Name} · {route.Address}" : "Шлюз IPv4 не найден",
            configuredAddress is not null || gateway.HasValue, history.ToArray());
    }

    internal static async Task<long?> PingOnce(IPAddress? address, CancellationToken token)
    {
        if (address is null) return null;
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(700), new byte[16], null, token);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    private (long Received, long Sent)? ReadTraffic(NetworkInterface? adapter)
    {
        if (adapter is null) return null;
        try
        {
            var stats = adapter.GetIPStatistics();
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var previous = previousTraffic;
            previousTraffic = (stats.BytesReceived, stats.BytesSent, now);
            if (previous is not { } last || stats.BytesReceived < last.Received || stats.BytesSent < last.Sent) return null;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(last.Tick, now).TotalSeconds;
            if (elapsed <= 0) return null;
            return ((long)((stats.BytesReceived - last.Received) * .008 / elapsed),
                (long)((stats.BytesSent - last.Sent) * .008 / elapsed));
        }
        catch (NetworkInformationException) { return null; }
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

    internal static (IPAddress Address, NetworkInterface Adapter)? DefaultGateway()
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
                    if (gateway is not null) return (gateway, adapter);
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
