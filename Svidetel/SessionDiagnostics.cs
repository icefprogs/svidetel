using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Security.Cryptography;

namespace Svidetel;

internal sealed class SessionDiagnostics
{
    private readonly int sessionId;
    private Dictionary<(int Id, long Started), (double Cpu, ulong Io)> previous = [];
    private long sampledAt;

    internal SessionDiagnostics()
    {
        using var self = Process.GetCurrentProcess();
        sessionId = self.SessionId;
    }

    internal (ProcessLoad[] Top, string[] Started) Observe(int game)
    {
        Dictionary<(int, long), (double, ulong)> snapshot = [];
        List<ProcessLoad> loads = [];
        List<string> started = [];
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(sampledAt, now).TotalSeconds;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0 || process.Id == Environment.ProcessId || process.Id == game || process.SessionId != sessionId)
                        continue;
                    var id = (process.Id, process.StartTime.ToUniversalTime().Ticks);
                    var cpu = process.TotalProcessorTime.TotalSeconds;
                    ulong io = GetProcessIoCounters(process.SafeHandle, out var counters) ? counters.Read + counters.Write : 0;
                    snapshot[id] = (cpu, io);
                    if (previous.TryGetValue(id, out var current) && elapsed > 0)
                    {
                        var load = Math.Max(0, (cpu - current.Cpu) / elapsed / Environment.ProcessorCount * 100);
                        var throughput = io >= current.Io ? (long)((io - current.Io) / elapsed / 1024) : 0;
                        if (load >= .1 || throughput >= 16) loads.Add(new(process.ProcessName, Math.Round(load, 1), throughput));
                    }
                    else if (sampledAt != 0 && started.Count < 6) started.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
        sampledAt = now;
        previous = snapshot;
        var top = loads.OrderByDescending(p => p.Cpu).Take(3)
            .Concat(loads.OrderByDescending(p => p.IoKbps).Take(3)).Distinct().ToArray();
        return (top, started.Distinct().ToArray());
    }

    internal static string? OnBattery() => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus switch
    {
        System.Windows.Forms.PowerLineStatus.Offline => "Работа от батареи",
        System.Windows.Forms.PowerLineStatus.Online => "Питание от сети",
        _ => null
    };

    internal static GameEnvironment EnvironmentFor(string executable, string? config)
    {
        string? version = null, driver = null, power = null, hash = null;
        if (File.Exists(executable))
        {
            try { version = FileVersionInfo.GetVersionInfo(executable).FileVersion; }
            catch (Exception ex) when (ex is IOException or Win32Exception) { }
        }
        try
        {
            using var display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is not null)
            {
                var versions = new List<string>();
                foreach (var name in display.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).Take(20))
                {
                    using var adapter = display.OpenSubKey(name);
                    if (adapter?.GetValue("DriverVersion") is string value) versions.Add(value);
                }
                if (versions.Count > 0) driver = string.Join(", ", versions.Distinct().Order());
            }
            power = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme", null) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try
        {
            if (config is not null && new FileInfo(config).Length <= 512_000)
            {
                using var stream = File.OpenRead(config);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(version, driver, power, hash);
    }

    internal static string[] WindowsEvents(MatchResult report)
    {
        var duration = report.Points.LastOrDefault()?.Second ?? 0;
        var end = report.At.AddSeconds(duration);
        var events = new List<string>();
        Read("Application", "*[System[(EventID=1000 and Provider[@Name='Application Error']) or (EventID=1002 and Provider[@Name='Application Hang'])]]",
            requireGame: true);
        Read("System", "*[System[(EventID=4101) and Provider[@Name='Display']]]", requireGame: false);
        return events.ToArray();

        void Read(string log, string xpath, bool requireGame)
        {
            if (requireGame && string.IsNullOrEmpty(report.Game)) return;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
                for (int i = 0; i < 80; i++)
                {
                    using var entry = reader.ReadEvent();
                    if (entry is null) break;
                    if (entry.TimeCreated is not { } time || time > end.AddSeconds(45)) continue;
                    if (time < end.AddSeconds(-60)) break;
                    var title = requireGame ? Path.GetFileName(report.Game) : "Сброс видеодрайвера";
                    if (requireGame && !entry.Properties.Any(p => string.Equals(p.Value as string, title, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Value as string, report.Game, StringComparison.OrdinalIgnoreCase))) continue;
                    events.Add($"{time:HH:mm:ss} · {title} · событие {entry.Id}");
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            { events.Add($"Журнал {log} недоступен"); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, Read, Write, Other;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out IoCounters counters);

    internal static unsafe int? CpuFrequency()
    {
        var count = GetActiveProcessorCount(ushort.MaxValue);
        if (count is 0 or > 1024) return null;
        Span<ProcessorPower> means = stackalloc ProcessorPower[(int)count];
        means.Clear();
        fixed (ProcessorPower* buffer = means)
        {
            if (CallNtPowerInformation(11, 0, 0, buffer, (uint)(means.Length * sizeof(ProcessorPower))) != 0) return null;
        }
        long mhz = 0;
        int reported = 0;
        foreach (var reading in means)
        {
            if (reading.CurrentMhz == 0) continue;
            mhz += reading.CurrentMhz;
            reported++;
        }
        return reported > 0 ? (int)(mhz / reported) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPower
    {
        internal uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("powrprof.dll")] private static extern unsafe int CallNtPowerInformation(int level, nint input, uint inputLength,
        ProcessorPower* output, uint outputLength);

    internal static uint? ConnectedControllers()
    {
        uint result = 0;
        try
        {
            for (uint controller = 0; controller < 4; controller++)
                if (XInputGetState(controller, out _) == 0) result |= 1u << (int)controller;
            return result;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct ControllerState;

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out ControllerState state);
}
