using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Svidetel;

internal sealed partial class PerformanceProfile(Engine engine)
{
    private const string Journal = "performance-session.json";
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private readonly object gate = new();
    private readonly HashSet<(int Pid, long Started)> denied = new();
    private FileStream? lease;
    private Changes? current;
    private sealed record Changes(List<Finding> Lowered, Guid? OriginalPower, List<Finding>? Raised = null);
    internal enum PowerResult { Off, Changed, AlreadyActive, Offline, Unavailable }
    internal enum GameState { Waiting, Restricted, Observing, Boosted, AlreadyConfigured }
    internal sealed record GameReading(GameState State, int Pid = 0, long StartedTicks = 0,
        TimeSpan? CpuTime = null, long? Memory = null);
    internal sealed record Result(int Lowered, int CloseRequests, PowerResult Power, int Skipped, string[] Details);
    internal sealed record Progress(int Done, int Total, string Name);

    internal Result Begin(bool optimizeBackground, bool closeApps, bool preferPerformance, IProgress<Progress> progress)
    {
        lease = Acquire();
        try
        {
            RestoreCore();
            denied.Clear();
            var candidates = optimizeBackground || closeApps ? Candidates() : [];
            bool pluggedIn = preferPerformance && Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online;
            var activePower = preferPerformance ? ReadPower() : null;
            var previousPower = pluggedIn && activePower != HighPerformance ? activePower : null;
            var power = !preferPerformance ? PowerResult.Off
                : activePower == HighPerformance ? PowerResult.AlreadyActive
                : pluggedIn ? PowerResult.Unavailable : PowerResult.Offline;
            var changes = new Changes([], previousPower, []);
            engine.Save(Journal, changes);

            var powerChanged = previousPower.HasValue && SetPower(HighPerformance);
            if (powerChanged) power = PowerResult.Changed;
            if (!powerChanged && previousPower.HasValue)
            {
                changes = changes with { OriginalPower = null };
                engine.Save(Journal, changes);
            }

            int lowered = 0, closed = 0, skipped = 0;
            var details = new List<string>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                progress.Report(new(i, candidates.Count, candidate.Name));
                try
                {
                    using var process = InteractiveProcess.OpenVerified(candidate, requireWindow: false);
                    if (process is null) { skipped++; continue; }
                    if (closeApps && process.MainWindowHandle != 0 && process.CloseMainWindow())
                    {
                        closed++;
                        details.Add($"{candidate.Name} · запрос закрытия окна");
                        continue;
                    }
                    if (!optimizeBackground || process.PriorityClass != ProcessPriorityClass.Normal) continue;
                    changes.Lowered.Add(candidate);
                    engine.Save(Journal, changes);
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered++;
                    details.Add($"{candidate.Name} · снижен приоритет");
                }
                catch (Exception ex) when (Unavailable(ex)) { skipped++; }
            }
            current = changes;
            return new(lowered, closed, power, skipped, details.Distinct().Take(24).ToArray());
        }
        catch
        {
            try { RestoreCore(); }
            finally { Release(); }
            throw;
        }
    }

    internal void Recover()
    {
        if (!File.Exists(Path.Combine(engine.DataDirectory, Journal))) return;
        using var recovery = Acquire();
        RestoreCore();
    }

    internal void End()
    {
        lock (gate)
        {
            if (lease is null) return;
            current = null;
            try { RestoreCore(); }
            finally { Release(); }
        }
    }

    internal GameReading ObserveGame(string executable, bool boost)
    {
        lock (gate)
        {
            if (current is null || !GameTarget.CanSelect(executable)) return new(GameState.Waiting);
            GameReading? best = null;
            Finding? target = null;
            bool restricted = false;
            var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
            foreach (var candidate in candidates)
            {
                using (candidate)
                {
                    if (candidate.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (!string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                        var item = new Finding { Kind = "process", Name = candidate.ProcessName, ProcessId = candidate.Id,
                            ProcessStartedAt = candidate.StartTime.ToUniversalTime(), Executable = executable };
                        using var process = InteractiveProcess.OpenVerified(item, requireWindow: false);
                        if (process is null) { restricted = true; continue; }
                        var reading = new GameReading(GameState.Observing, process.Id,
                            item.ProcessStartedAt.Value.UtcTicks, process.TotalProcessorTime, process.WorkingSet64);
                        if (best is not null && best.Memory >= reading.Memory) continue;
                        best = reading;
                        target = item;
                    }
                    catch (Exception ex) when (Unavailable(ex)) { restricted = true; }
                }
            }
            if (best is null || target is null) return new(restricted ? GameState.Restricted : GameState.Waiting);
            if (!boost) return best;
            try
            {
                using var process = InteractiveProcess.OpenVerified(target, requireWindow: false);
                return best with { State = process is null ? GameState.Restricted : BoostGame(current, target, process) };
            }
            catch (Exception ex) when (Unavailable(ex)) { return best with { State = GameState.Restricted }; }
        }
    }

    private GameState BoostGame(Changes changes, Finding item, Process process)
    {
        var raised = changes.Raised!;
        var key = (item.ProcessId, item.ProcessStartedAt!.Value.UtcTicks);
        if (denied.Contains(key)) return GameState.Restricted;
        var priority = process.PriorityClass;
        var tracked = raised.Any(saved => saved.ProcessId == item.ProcessId && saved.ProcessStartedAt == item.ProcessStartedAt &&
            string.Equals(saved.Executable, item.Executable, StringComparison.OrdinalIgnoreCase));
        if (tracked) return priority == ProcessPriorityClass.AboveNormal ? GameState.Boosted : GameState.AlreadyConfigured;
        if (priority != ProcessPriorityClass.Normal || raised.Count >= 128) return GameState.AlreadyConfigured;
        raised.Add(item);
        engine.Save(Journal, changes);
        try { process.PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch (Win32Exception)
        {
            raised.Remove(item);
            engine.Save(Journal, changes);
            denied.Add(key);
            return GameState.Restricted;
        }
        return GameState.Boosted;
    }

    private List<Finding> Candidates()
    {
        var result = new List<Finding>();
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!BackgroundApps.HasKnownName(process.ProcessName) || process.SessionId != self.SessionId ||
                        process.MainModule?.FileName is not { } path || !BackgroundApps.IsRecognized(path)) continue;
                    result.Add(new Finding
                    {
                        Kind = "process", ProcessId = process.Id, ProcessStartedAt = process.StartTime.ToUniversalTime(),
                        Name = process.ProcessName, Executable = path
                    });
                }
                catch (Exception ex) when (Unavailable(ex)) { }
            }
        }
        return result;
    }

    private void RestoreCore()
    {
        var changes = engine.Read<Changes>(Journal);
        if (changes is null) return;
        var pending = RestorePriorities(changes.Lowered, ProcessPriorityClass.BelowNormal, BackgroundApps.IsRecognized);
        var raised = RestorePriorities(changes.Raised ?? [], ProcessPriorityClass.AboveNormal, GameTarget.CanSelect);
        Guid? power = null;
        if (changes.OriginalPower is { } original)
        {
            var active = ReadPower();
            if (!active.HasValue || active == HighPerformance && !SetPower(original)) power = original;
        }
        if (pending.Count > 0 || raised.Count > 0 || power.HasValue)
        {
            engine.Save(Journal, new Changes(pending, power, raised));
            throw new IOException("Часть настроек ещё не восстановлена. Закройте и снова откройте «Свидетель», чтобы повторить восстановление.");
        }
        File.Delete(Path.Combine(engine.DataDirectory, Journal));
    }

    private static List<Finding> RestorePriorities(IEnumerable<Finding> items, ProcessPriorityClass assigned, Func<string, bool> allowed)
    {
        var pending = new List<Finding>();
        foreach (var item in items)
        {
            try
            {
                if (!allowed(item.Executable)) continue;
                using var process = InteractiveProcess.OpenVerified(item, requireWindow: false);
                if (process?.PriorityClass == assigned)
                    process.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (ArgumentException) { } // The original process exited.
            catch (InvalidOperationException) { } // The process exited during verification.
            catch (Exception ex) when (Unavailable(ex)) { pending.Add(item); }
        }
        return pending;
    }

    private FileStream Acquire() => File.Open(Path.Combine(engine.DataDirectory, "performance-session.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void Release() { lease?.Dispose(); lease = null; }

    private static bool Unavailable(Exception ex) => ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException;

    private static Guid? ReadPower()
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static bool SetPower(Guid scheme) => PowerSetActiveScheme(0, in scheme) == 0;

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRoot, out nint scheme);
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRoot, in Guid scheme);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
