namespace Svidetel;

internal enum NetworkObservation { Warming, Responding, RouterSilent, WanSilent, BothSilent, Drops }

internal sealed class PassiveNetworkWindow
{
    private readonly List<(DateTimeOffset At, GameTelemetry.Sample Sample)> history = [];
    private string? routeKey;

    internal void Reset()
    {
        routeKey = null;
        history.Clear();
    }

    internal void Add(string key, DateTimeOffset at, GameTelemetry.Sample sample)
    {
        if (routeKey != key)
        {
            Reset();
            routeKey = key;
        }
        history.RemoveAll(item => at - item.At > TimeSpan.FromMinutes(10));
        history.Add((at, sample));
        if (history.Count > 150) history.RemoveAt(0);
    }

    internal GameTelemetry.Sample[] Recent() => history.TakeLast(8).Select(item => item.Sample).ToArray();

    internal (GameTelemetry.Sample[] Idle, GameTelemetry.Sample[] Loaded) Cohorts()
    {
        var readings = history.Select(item => item.Sample).Where(sample => (sample.RemoteProbed || sample.RouterProbed)
            && sample.ReceiveKbps.HasValue && sample.SendKbps.HasValue);
        return (readings.Where(sample => sample.ReceiveKbps + sample.SendKbps <= 192).TakeLast(12).ToArray(),
            readings.Where(sample => Math.Max(sample.ReceiveKbps!.Value, sample.SendKbps!.Value) >= 1000).TakeLast(12).ToArray());
    }

    internal static long?[] Results(IEnumerable<GameTelemetry.Sample> points, bool router) => router
        ? points.Where(point => point.RouterProbed).Select(point => point.Router).ToArray()
        : points.Where(point => point.RemoteProbed).Select(point => point.Latency).ToArray();

    internal static NetworkObservation Evaluate(long?[] router, long?[] external)
    {
        if (Math.Max(router.Length, external.Length) < 4) return NetworkObservation.Warming;
        bool routerSilent = router.Length >= 4 && router.TakeLast(4).Count(ms => !ms.HasValue) >= 3;
        bool wanSilent = external.Length >= 4 && external.TakeLast(4).Count(ms => !ms.HasValue) >= 3;
        if (routerSilent && wanSilent) return NetworkObservation.BothSilent;
        if (routerSilent && external.TakeLast(4).Count(ms => ms.HasValue) >= 3) return NetworkObservation.RouterSilent;
        if (wanSilent && router.TakeLast(4).Count(ms => ms.HasValue) >= 3) return NetworkObservation.WanSilent;
        return router.Concat(external).Any(ms => !ms.HasValue) ? NetworkObservation.Drops : NetworkObservation.Responding;
    }

    internal static double Median(long[] sorted) => (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2.0;
}
