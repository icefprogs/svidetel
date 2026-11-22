using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Svidetel;

internal sealed partial class GameTelemetry(IPAddress? configuredAddress = null, bool automaticInternet = false)
{
    internal sealed record Sample(long? Latency, int? Cpu, long? Router = null, bool RemoteProbed = false,
        string? Adapter = null, long? ReceiveKbps = null, long? SendKbps = null, bool RouterProbed = false);
    internal sealed record Snapshot(string Source, bool IsMeasuringNetwork, Sample[] History, NetworkConnection? Connection = null)
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
    private ActiveNetwork? route;
    private IPAddress? automaticAddress;
    private (long Received, long Sent, long Tick)? previousTraffic;
    private int routeTurns;
    private int missedReference;
    private long lastBackupCheck;

    internal async Task<Snapshot> Measure(CancellationToken token, bool refreshRoute = false)
    {
        if (routeTurns++ % 8 == 0 || refreshRoute)
        {
            var current = NetworkRoutes.Find(configuredAddress);
            if (current?.Key != route?.Key)
            {
                history.Clear();
                previousTraffic = null;
                automaticAddress = current?.Reference;
                missedReference = 0;
                lastBackupCheck = 0;
            }
            route = current;
        }

        var cpu = ReadCpu();
        var traffic = ReadTraffic(route?.Adapter);
        var automatic = configuredAddress is null && automaticInternet;
        var target = configuredAddress ?? (automatic ? automaticAddress : null);
        var router = route?.Router;
        var routerTask = PingOnce(router, token);
        var remoteTask = target is null || Equals(target, router) ? routerTask : PingOnce(target, token);
        await Task.WhenAll(routerTask, remoteTask);
        var remote = await remoteTask;
        if (automatic && target is not null && route is { } currentRoute)
        {
            missedReference = remote.HasValue ? 0 : missedReference + 1;
            if (missedReference >= 3 && (lastBackupCheck == 0 ||
                System.Diagnostics.Stopwatch.GetElapsedTime(lastBackupCheck) >= TimeSpan.FromMinutes(1)))
            {
                lastBackupCheck = System.Diagnostics.Stopwatch.GetTimestamp();
                var backup = target.Equals(currentRoute.Reference) ? currentRoute.Alternate : currentRoute.Reference;
                if (await PingOnce(backup, token) is { } available)
                {
                    history.Clear();
                    automaticAddress = target = backup;
                    remote = available;
                    missedReference = 0;
                }
            }
        }
        history.Enqueue(new(remote, cpu, await routerTask, target is not null,
            route?.Adapter.Name, traffic?.Received, traffic?.Sent, router is not null));
        if (history.Count > 24) history.Dequeue();
        var connection = route is null ? null : new NetworkConnection($"{route.Key}|{target}", route.Adapter.Id,
            route.Adapter.Name, route.Adapter.NetworkInterfaceType, route.Adapter.Speed, router, target, automatic,
            route.Addresses, route.Dns, route.RouterAdapter);
        return new(target is not null ? $"{(automatic ? "Контрольный узел" : "Указанный IP")} · {target}"
                : router is not null ? $"{route!.Adapter.Name} · {router}" : "Шлюз не найден",
            target is not null || router is not null, history.ToArray(), connection);
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

    [LibraryImport("kernel32.dll")]
    private static partial int GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
