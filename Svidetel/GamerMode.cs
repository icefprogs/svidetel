using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Svidetel;

public partial class MainWindow
{
    private enum ProfileMode { Off, Background, Gamer }
    private ProfileMode activeProfile;
    private PerformanceProfile performance = null!;
    private CancellationTokenSource? gameMonitor;
    private bool changingProfile;

    private void InitializePerformance()
    {
        performance = new(engine);
        if (captureDemo || demo) return;
        try { performance.Recover(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Text = "Восстановление прошлого режима: " + ex.Message;
        }
        Closing += (_, e) =>
        {
            if (changingProfile) { e.Cancel = true; return; }
            StopMeasurements();
            try { performance.End(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Восстановление настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private async void QuickOptimizeClick(object sender, RoutedEventArgs e) => await ToggleProfile(ProfileMode.Background);
    private async void GamerModeClick(object sender, RoutedEventArgs e) => await ToggleProfile(ProfileMode.Gamer);

    private async Task ToggleProfile(ProfileMode requested)
    {
        if (requested == ProfileMode.Gamer) Sections.SelectedItem = GamerTab;
        if (demo)
        {
            if (requested == ProfileMode.Gamer) ShowGameDemo();
            Status.Text = "Деморежим · настройки Windows остаются прежними";
            return;
        }
        if (busy || changingProfile) return;
        changingProfile = true;
        ProfileProgress.Visibility = Visibility.Visible;
        ProfileProgressBar.IsIndeterminate = true;
        ProfileProgressText.Text = "Проверяю фоновые процессы…";
        try
        {
            await Work(async () =>
            {
                var previous = activeProfile;
                if (previous != ProfileMode.Off)
                {
                    ProfileProgressText.Text = "Восстанавливаю предыдущие настройки…";
                    StopMeasurements();
                    activeProfile = ProfileMode.Off;
                    await Task.Run(performance.End);
                    Status.Text = "Приоритеты и план питания восстановлены";
                    GamerSummary.Text = "Игровая сессия завершена. На экране остались последние измерения.";
                }
                if (previous == requested) return;

                bool gaming = requested == ProfileMode.Gamer;
                bool close = gaming && CloseBackgroundApps.IsChecked == true;
                bool power = gaming && PreferPower.IsChecked == true;
                var progress = new Progress<PerformanceProfile.Progress>(step =>
                {
                    if (!changingProfile) return;
                    ProfileProgressBar.IsIndeterminate = false;
                    ProfileProgressBar.Maximum = step.Total;
                    ProfileProgressBar.Value = step.Done;
                    ProfileProgressText.Text = $"{step.Done + 1} из {step.Total} · {step.Name}";
                });
                var result = await Task.Run(() => performance.Begin(close, power, progress));
                if (!gaming && result.Lowered == 0)
                {
                    await Task.Run(performance.End);
                    Status.Text = "Подходящих фоновых процессов сейчас нет";
                    return;
                }
                activeProfile = requested;
                var summary = $"Понижен приоритет: {result.Lowered} · Запросы закрытия: {result.CloseRequests}";
                if (result.PowerChanged) summary += "\nВключён план «Высокая производительность»";
                if (power && !result.PowerChanged) summary += "\nИсходный план питания сохранён";
                if (result.Skipped > 0) summary += $"\nПропущено из-за доступа или выхода: {result.Skipped}";
                Status.Text = (gaming ? "Игровая сессия активна" : "Фоновая оптимизация активна") + $" · Изменён приоритет: {result.Lowered}";
                if (!gaming) return;
                GamerSummary.Text = summary + "\n\nSteam, Discord, игры и WinDivert остаются активными.";
                GamerSource.Text = "Подключаюсь к шлюзу…";
                _ = MeasureGame();
            }, "Применяю параметры фоновой нагрузки…");
        }
        finally
        {
            changingProfile = false;
            ProfileProgress.Visibility = Visibility.Collapsed;
            OptimizeButton.Content = activeProfile == ProfileMode.Background ? "Отменить" : "Оптимизация";
            GamerButton.Content = activeProfile == ProfileMode.Gamer ? "Завершить режим" : "Режим геймера";
        }
    }

    private async Task MeasureGame()
    {
        using var cancellation = new CancellationTokenSource();
        gameMonitor = cancellation;
        var telemetry = new GameTelemetry();
        try
        {
            using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            do { ShowGameSnapshot(await telemetry.Measure(cancellation.Token)); }
            while (await clock.WaitForNextTickAsync(cancellation.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GamerSource.Text = "Измерения приостановлены: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(gameMonitor, cancellation)) gameMonitor = null;
        }
    }

    private void StopMeasurements()
    {
        var running = gameMonitor;
        gameMonitor = null;
        running?.Cancel();
    }

    private void ShowGameSnapshot(GameTelemetry.Snapshot snapshot)
    {
        GamerSource.Text = snapshot.Source;
        GamerLatency.Text = snapshot.Median is { } latency ? $"{latency} мс" : "—";
        GamerJitter.Text = snapshot.Jitter is { } jitter ? $"{jitter:0.#} мс" : "—";
        GamerMissing.Text = snapshot.IsMeasuringNetwork ? $"{snapshot.Missing}/{snapshot.History.Length}" : "—";
        GamerCpu.Text = snapshot.History.LastOrDefault()?.Cpu is { } cpu ? $"{cpu}%" : "—";
        var maximum = Math.Max(10, snapshot.History.Select(x => x.Latency).OfType<long>().DefaultIfEmpty(0).Max());
        var scale = Math.Ceiling(maximum / 10.0) * 10;
        GraphScale.Text = snapshot.IsMeasuringNetwork ? $"{scale:0} мс" : "";
        var trace = new PathGeometry();
        var breaks = new PathGeometry();
        PathFigure? line = null;
        for (var i = 0; i < snapshot.History.Length; i++)
        {
            var x = 495.0 * i / Math.Max(1, snapshot.History.Length - 1) + 2;
            if (snapshot.History[i].Latency is not { } ms)
            {
                if (snapshot.IsMeasuringNetwork)
                    breaks.Figures.Add(new PathFigure(new Point(x, 93), [new LineSegment(new Point(x, 103), true)], false));
                line = null;
                continue;
            }
            var point = new Point(x, 96 - 81 * ms / scale);
            if (line is null)
            {
                line = new PathFigure { StartPoint = point };
                line.Segments.Add(new LineSegment(new Point(x + 1, point.Y), true));
                trace.Figures.Add(line);
            }
            else line.Segments.Add(new LineSegment(point, true));
        }
        GamerPlot.Data = trace;
        GamerTimeouts.Data = breaks;
        var spike = snapshot.Median is { } baseline && maximum >= Math.Max(20, 4 * baseline);
        GamerNote.Text = !snapshot.IsMeasuringNetwork ? "На выбранном маршруте нельзя измерить задержку до шлюза. Загрузка CPU по-прежнему доступна."
            : snapshot.History.Length >= 5 && snapshot.Median is null ? "Шлюз не отвечает на ICMP. Если интернет работает, роутер может игнорировать такие запросы."
            : snapshot.History.Length >= 5 && (snapshot.Missing >= 2 || snapshot.Jitter >= 12 || spike) ?
                "Есть скачки или пропуски до шлюза. Если играешь по Wi‑Fi, проверь связь по кабелю и приостанови крупные загрузки."
            : snapshot.History.TakeLast(3).Count(x => x.Cpu >= 90) >= 3 ?
                "CPU загружен на 90% и выше несколько замеров подряд. Проверь фоновые задачи в Диспетчере задач."
            : "Здесь виден участок до роутера. Задержку до сервера игры смотри в самой игре.";
    }

    private void ShowGameDemo()
    {
        long?[] samples = [4, 4, 5, 4, 7, 6, 5, 4, 4, 6, 24, 31, 15, 7, null, 5, 4, 5, 4, 6, 5, 4, 4, 5];
        ShowGameSnapshot(new("Ethernet · демонстрационный шлюз", true, samples.Select(ms => new GameTelemetry.Sample(ms, 37)).ToArray()));
        GamerSummary.Text = "Демонстрация\nПонижен приоритет: 2 · Запросы закрытия: 0\n\nSteam, Discord, игры и WinDivert остаются активными.";
    }

    private void WindowsGameSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:gaming-gamemode");
    private void WindowsDownloadSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:delivery-optimization-advanced");

    private void OpenSettingsPage(string address)
    {
        try { using var launched = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Status.Text = ex.Message; }
    }
}
