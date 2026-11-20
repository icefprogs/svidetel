using System.Diagnostics;
using System.Windows;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record DefenderEvent(string When, string Name, int Id);
    private sealed record DefenderReport(bool? Enabled, bool? RealTime, string[] Errors, DefenderEvent[] Events);
    private bool readingDefender;
    private System.Windows.Threading.DispatcherTimer? defenderTimer;
    private HashSet<int>? previousProcesses;
    private readonly Queue<string> recentProcesses = new();

    private void StartDefenderDashboard()
    {
        if (captureDemo || demo) return;
        ObserveProcesses();
        defenderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        defenderTimer.Tick += async (_, _) =>
        {
            ObserveProcesses();
            if (activeProfile != ProfileMode.Gamer || Sections.SelectedItem == DefenderTab) await RefreshDefender();
        };
        defenderTimer.Start();
        Closed += (_, _) => defenderTimer.Stop();
        _ = RefreshDefender();
    }

    private async void DefenderRefreshClick(object sender, RoutedEventArgs e) => await RefreshDefender();
    private void OpenDefenderClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:windowsdefender");

    private async void DefenderScanClick(object sender, RoutedEventArgs e)
    {
        if (busy || demo || captureDemo) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Выбери файл для проверки Defender", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Work(async () =>
        {
            await engine.Call<System.Text.Json.JsonElement>(new { op = "defender-scan-file", path = dialog.FileName });
            Status.Text = "Defender принял запрос. Результаты появятся в журнале защиты.";
            await RefreshDefender();
        }, "Передаю файл Microsoft Defender…");
    }

    private void ObserveProcesses()
    {
        var current = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                current.Add(process.Id);
                if (previousProcesses is null || previousProcesses.Contains(process.Id)) continue;
                try
                {
                    recentProcesses.Enqueue($"{DateTime.Now:HH:mm:ss} · {process.ProcessName} · PID {process.Id}");
                    if (recentProcesses.Count > 12) recentProcesses.Dequeue();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        previousProcesses = current;
        if (recentProcesses.Count > 0) RecentProcesses.Text = string.Join("\n", recentProcesses.Reverse());
    }

    private async Task RefreshDefender()
    {
        if (readingDefender || captureDemo || demo) return;
        readingDefender = true;
        try
        {
            var report = await engine.Call<DefenderReport>(new { op = "defender-status" });
            DefenderTitle.Text = report.Enabled == true && report.RealTime == true ? "Защита в реальном времени включена"
                : report.RealTime == false ? "Защита Defender в реальном времени выключена" : "Статус Defender недоступен";
            DefenderEvents.Text = report.Events.Length == 0 ? "За последние 7 дней событий в доступном журнале нет."
                : string.Join("\n", report.Events.Select(x => $"{x.When} · {(x.Id == 1117 ? "Действие Defender" : "Обнаружение")} · {x.Name}"));
            DefenderError.Text = string.Join("\n", report.Errors);
        }
        catch (Exception ex) { DefenderError.Text = "Журнал Defender: " + ex.Message; }
        finally { readingDefender = false; }
    }
}
