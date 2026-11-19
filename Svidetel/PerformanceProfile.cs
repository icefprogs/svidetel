using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Svidetel;

internal sealed partial class PerformanceProfile(Engine engine)
{
    private const string Journal = "performance-session.json";
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private FileStream? lease;
    private sealed record Changes(List<Finding> Lowered, Guid? OriginalPower);
    internal sealed record Result(int Lowered, int CloseRequests, bool PowerChanged, int Skipped);
    internal sealed record Progress(int Done, int Total, string Name);

    internal Result Begin(bool closeApps, bool preferPerformance, IProgress<Progress> progress)
    {
        lease = Acquire();
        try
        {
            RestoreCore();
            var candidates = Candidates();
            var previousPower = preferPerformance && Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online
                ? ReadPower() : null;
            if (previousPower == HighPerformance) previousPower = null;
            var changes = new Changes([], previousPower);
            engine.Save(Journal, changes);

            var powerChanged = previousPower.HasValue && SetPower(HighPerformance);
            if (!powerChanged && previousPower.HasValue)
            {
                changes = changes with { OriginalPower = null };
                engine.Save(Journal, changes);
            }

            int lowered = 0, closed = 0, skipped = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                progress.Report(new(i, candidates.Count, candidate.Name));
                try
                {
                    using var process = InteractiveProcess.OpenVerified(candidate, requireWindow: false);
                    if (process is null) { skipped++; continue; }
                    if (closeApps && process.MainWindowHandle != 0 && process.CloseMainWindow()) { closed++; continue; }
                    if (process.PriorityClass != ProcessPriorityClass.Normal) continue;
                    changes.Lowered.Add(candidate);
                    engine.Save(Journal, changes);
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered++;
                }
                catch (Exception ex) when (Unavailable(ex)) { skipped++; }
            }
            return new(lowered, closed, powerChanged, skipped);
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
        if (lease is null) return;
        try { RestoreCore(); }
        finally { Release(); }
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
        var pending = new List<Finding>();
        foreach (var item in changes.Lowered)
        {
            try
            {
                if (!BackgroundApps.IsRecognized(item.Executable)) continue;
                using var process = InteractiveProcess.OpenVerified(item, requireWindow: false);
                if (process?.PriorityClass == ProcessPriorityClass.BelowNormal)
                    process.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (ArgumentException) { } // The original process exited.
            catch (InvalidOperationException) { } // The process exited during verification.
            catch (Exception ex) when (Unavailable(ex)) { pending.Add(item); }
        }
        Guid? power = null;
        if (changes.OriginalPower is { } original)
        {
            var active = ReadPower();
            if (!active.HasValue || active == HighPerformance && !SetPower(original)) power = original;
        }
        if (pending.Count > 0 || power.HasValue)
        {
            engine.Save(Journal, new Changes(pending, power));
            throw new IOException("Часть настроек ещё не восстановлена. Закройте и снова откройте «Свидетель», чтобы повторить восстановление.");
        }
        File.Delete(Path.Combine(engine.DataDirectory, Journal));
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
