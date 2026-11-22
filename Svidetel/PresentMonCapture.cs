using System.Diagnostics;
using System.Security.Cryptography;

namespace Svidetel;

internal sealed class PresentMonCapture : IDisposable
{
    internal const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    private static readonly string Binary = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
    private readonly CancellationTokenSource stop = new();
    private Task? running;

    internal static bool IsPackaged => File.Exists(Binary);

    internal void Start(int pid, MatchRecorder recorder, Action<string> status)
    {
        if (running is not null) throw new InvalidOperationException();
        running = Task.Run(() => Run(pid, recorder, status, stop.Token));
    }

    private static async Task Run(int pid, MatchRecorder recorder, Action<string> status, CancellationToken token)
    {
        var session = "Svidetel-" + Guid.NewGuid().ToString("N");
        Process? process = null;
        bool hadFrame = false;
        try
        {
            await using var binary = File.OpenRead(Binary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(binary, token));
            if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Контрольная сумма PresentMon изменилась.");
            var launch = Command("--process_id", pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--session_name", session, "--output_stdout", "--no_console_stats", "--no_track_input", "--qpc_time",
                "--v2_metrics", "--exclude_dropped", "--terminate_on_proc_exit", "--timed", "14400", "--terminate_after_timed");
            process = Process.Start(launch) ?? throw new IOException("PresentMon не запустился.");
            var stderr = process.StandardError.ReadToEndAsync();
            var reader = new FrameCsv(pid);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (reader.Parse(line) is not { } frame) continue;
                recorder.AddFrame(frame);
                if (!hadFrame) { hadFrame = true; status("PresentMon записывает кадры"); }
            }
            await process.WaitForExitAsync(token);
            if (!hadFrame)
            {
                var message = await stderr;
                status(message.Contains("access", StringComparison.OrdinalIgnoreCase) || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    ? "Windows не дала PresentMon доступ к ETW. CPU и сеть по-прежнему измеряются."
                    : "PresentMon не получил кадры выбранного процесса.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            status("Кадры недоступны: " + ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            using var closer = Process.Start(Command("--session_name", session, "--terminate_existing_session", "--no_console_stats"));
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await process.WaitForExitAsync(timeout.Token);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
                        {
                            if (!process.HasExited) process.Kill();
                        }
                    }
                }
            }
        }
    }

    private static ProcessStartInfo Command(params string[] args)
    {
        var info = new ProcessStartInfo(Binary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            if (running?.Wait(TimeSpan.FromMilliseconds(2600)) == false)
            {
                _ = running.ContinueWith(task => { _ = task.Exception; stop.Dispose(); }, TaskScheduler.Default);
                return;
            }
        }
        catch (AggregateException) { }
        stop.Dispose();
    }
}
