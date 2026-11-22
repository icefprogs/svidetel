using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace Svidetel;

internal sealed record MatchPoint(double Second, double? FrameMs, double? GpuMs, int? Cpu, long? Ping,
    long? Router = null, bool RemoteProbed = false, bool RouterProbed = false, int? GameMemoryMb = null,
    ProcessLoad[]? Background = null, long? ReceiveKbps = null, long? SendKbps = null, int? CpuMHz = null);
internal sealed record MatchResult(string Game, DateTimeOffset At, int Frames, double? Fps, double? Low1, double? P99,
    MatchPoint[] Points, double[] Marks, bool Limited, double[]? AutomaticMarks = null, int? LongFrames = null,
    MatchEvent[]? Events = null, GameEnvironment? Environment = null, string? Applied = null);
internal sealed record MatchEvent(double Second, string Text);
internal sealed record GameEnvironment(string? GameVersion, string? Driver, string? Power, string? ConfigHash);
internal sealed record ProcessLoad(string Name, double Cpu, long IoKbps);
internal sealed record LiveFrames(double Fps, double? Low1, double LastMs);
internal sealed record GameProfile(string Executable, string Probe, bool Boost, bool Lower, bool Close, bool Power, bool Automatic)
{
    public string Name => Path.GetFileNameWithoutExtension(Executable);
}
internal sealed record RunComparison(MatchResult? Before = null, MatchResult? After = null);

internal sealed class MatchRecorder(string game)
{
    private const int TimelineLimitSeconds = 14_400;
    private readonly object sync = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset at = DateTimeOffset.Now;
    private readonly Dictionary<string, List<double>> frames = new();
    private readonly Dictionary<string, FreezeDetector> detectors = new();
    private readonly Dictionary<(string Chain, int Slot), (double Peak, double GpuTotal, int GpuCount)> frameTimeline = new();
    private readonly List<MatchPoint> points = [];
    private readonly List<double> marks = [];
    private readonly List<MatchEvent> events = [];
    private int totalFrames;
    private bool limited;

    private double Second => Stopwatch.GetElapsedTime(started).TotalSeconds;

    internal void AddFrame(FrameLine frame)
    {
        lock (sync)
        {
            if (frames.TryGetValue(frame.Chain, out var times) || frames.Count < 8)
            {
                if (times is null) frames[frame.Chain] = times = [];
                if (totalFrames < 400_000) { times.Add(frame.FrameMs); totalFrames++; }
                else limited = true;
                var relative = frame.Qpc is { } tick ? Stopwatch.GetElapsedTime(started, tick).TotalSeconds : Second;
                if (!detectors.TryGetValue(frame.Chain, out var detector)) detectors[frame.Chain] = detector = new();
                detector.Observe(frame.FrameMs, relative);
                if (relative >= 0 && relative <= TimelineLimitSeconds)
                {
                    var key = (frame.Chain, (int)(relative / 1.5));
                    frameTimeline.TryGetValue(key, out var last);
                    frameTimeline[key] = (Math.Max(last.Peak, frame.FrameMs), last.GpuTotal + (frame.GpuMs ?? 0),
                        last.GpuCount + (frame.GpuMs.HasValue ? 1 : 0));
                }
            }
        }
    }

    internal void AddSample(int? cpu, long? ping, long? router = null, bool remoteProbed = false, bool routerProbed = false,
        int? gameMemoryMb = null, ProcessLoad[]? background = null, long? receiveKbps = null, long? sendKbps = null,
        int? cpuMHz = null)
    {
        lock (sync)
        {
            // Limit the timeline's size while retaining points from any saved freeze window.
            var seconds = Second;
            if (points.Count >= 1600)
            {
                var dominant = detectors.Values.MaxBy(detector => detector.Count);
                int oldest = points.FindIndex(30, p => !marks.Any(m => Math.Abs(p.Second - m) <= 8) &&
                    (dominant is null || !dominant.Marks.Any(m => Math.Abs(p.Second - m) <= 8)));
                if (oldest >= 0) points.RemoveAt(oldest);
            }
            if (points.Count < 1600) points.Add(new(seconds, null, null, cpu, ping, router, remoteProbed, routerProbed,
                gameMemoryMb, background, receiveKbps, sendKbps, cpuMHz));
        }
    }

    internal bool MarkFreeze()
    {
        lock (sync)
        {
            var second = Second;
            if (marks.Count >= 24 || marks.Count > 0 && second - marks[^1] < 1) return false;
            marks.Add(second);
            return true;
        }
    }

    internal void AddEvent(string message)
    {
        lock (sync)
        {
            if (events.Count < 240) events.Add(new(Second, message));
        }
    }

    internal LiveFrames? Live()
    {
        lock (sync)
        {
            var dominant = detectors.Values.MaxBy(detector => detector.Count);
            return dominant?.Live();
        }
    }

    internal MatchResult Finish()
    {
        lock (sync)
        {
            var primary = frames.Count == 0 ? default : frames.MaxBy(x => x.Value.Count);
            var times = primary.Value?.OrderDescending().ToArray() ?? [];
            var slowCount = (int)Math.Ceiling(times.Length * 0.01);
            var eligible = times.Length >= 100 && !limited;
            var auto = primary.Key is not null
                ? detectors[primary.Key].Marks.Where(mark => marks.All(m => Math.Abs(mark - m) > 2)).ToArray()
                : [];
            var allMarks = marks.Concat(auto).Order().ToArray();
            var aligned = points.Select(p => primary.Key is not null &&
                frameTimeline.TryGetValue((primary.Key, (int)(p.Second / 1.5)), out var sample)
                ? p with { FrameMs = sample.Peak, GpuMs = sample.GpuCount > 0 ? sample.GpuTotal / sample.GpuCount : null } : p).ToArray();
            return new(game, at, times.Length, times.Length == 0 || limited ? null : 1000 / times.Average(),
                eligible ? 1000 / times.Take(slowCount).Average() : null,
                eligible ? times[times.Length - (int)Math.Ceiling(times.Length * .99)] : null, aligned, allMarks, limited,
                auto, limited || times.Length == 0 ? null : times.Count(time => time >= 50), events.ToArray());
        }
    }
}

internal sealed class FreezeDetector
{
    private readonly Queue<double> recent = new(120);
    private readonly List<double> marks = [];
    private int turns;
    private double baseline;

    internal int Count { get; private set; }
    internal IReadOnlyList<double> Marks => marks;

    internal void Observe(double interval, double second)
    {
        Count++;
        // PresentMon timestamps refer to frame start. A newly launched game needs a baseline first.
        if (second >= 0 && recent.Count == 120 && marks.Count < 24 &&
            interval >= Math.Max(50, baseline * 3) && (marks.Count == 0 || second - marks[^1] >= 2))
        {
            marks.Add(second);
        }
        if (recent.Count == 120) recent.Dequeue();
        recent.Enqueue(interval);
        if (++turns % 30 == 0)
            baseline = recent.Order().ElementAt(recent.Count / 2);
    }

    internal LiveFrames? Live()
    {
        if (recent.Count < 10) return null;
        return new(1000 / recent.Average(),
            recent.Count >= 100 ? 1000 / recent.OrderDescending().Take((int)Math.Ceiling(recent.Count * .01)).Average() : null,
            recent.Last());
    }
}

internal sealed record FrameLine(string Chain, double FrameMs, double? GpuMs, long? Qpc = null);

internal sealed class FrameCsv(int target)
{
    private int pid = -1, chain = -1, frame = -1, gpu = -1, clock = -1;

    internal FrameLine? Parse(string line)
    {
        var cells = line.Split(',');
        if (cells.Length == 0) return null;
        if (cells[0].Trim('"', '\uFEFF').Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            pid = Column("ProcessID");
            chain = Column("SwapChainAddress");
            frame = Column("FrameTime");
            gpu = Column("GPUBusy");
            clock = Column("CPUStartQPC");
            return null;
        }
        if (pid < 0 || chain < 0 || frame < 0 || cells.Length <= Math.Max(Math.Max(pid, chain), frame) ||
            !int.TryParse(cells[pid], out var process) || process != target || Number(frame) is not { } interval || interval <= 0) return null;
        return new(cells[chain], interval, gpu >= 0 && gpu < cells.Length ? Number(gpu) : null,
            clock >= 0 && clock < cells.Length && long.TryParse(cells[clock], out var tick) ? tick : null);

        int Column(string name) => Array.FindIndex(cells, c => c.Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase));
        double? Number(int index) => double.TryParse(cells[index].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0 ? value : null;
    }
}

internal sealed record ServerReading(string Label, string Address, int Replies, long? Median, long? Worst);
internal static class ServerProbe
{
    internal static Task<ServerReading[]> Compare(IEnumerable<(string Label, IPAddress Address)> endpoints, CancellationToken token)
        => Task.WhenAll(endpoints.Select(endpoint => Read(endpoint.Label, endpoint.Address, token)));

    private static async Task<ServerReading> Read(string label, IPAddress address, CancellationToken token)
    {
        using var ping = new Ping();
        List<long> received = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(950), new byte[16], null, token);
                if (reply.Status == IPStatus.Success) received.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            if (attempt < 3) await Task.Delay(200, token);
        }
        received.Sort();
        return new(label, address.ToString(), received.Count,
            received.Count == 0 ? null : (received[(received.Count - 1) / 2] + received[received.Count / 2]) / 2,
            received.Count == 0 ? null : received[^1]);
    }
}
