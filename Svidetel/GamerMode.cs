using System.Diagnostics;
using System.Net;
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

    private void ChooseGameClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Выберите исполняемый файл игры", Filter = "Игра (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!GameTarget.CanSelect(picker.FileName))
        {
            GameProcessStatus.Text = "Этот файл нельзя выбрать. Укажите EXE игры на локальном диске.";
            return;
        }
        preferences = preferences with { GamePath = picker.FileName, ProbeAddress = ProbeIp.Text.Trim() };
        UpdateGameSelection();
        SavePreferences();
    }

    private void ClearGameClick(object sender, RoutedEventArgs e)
    {
        preferences = preferences with { GamePath = "", ProbeAddress = ProbeIp.Text.Trim(), BoostGame = false };
        BoostSelectedGame.IsChecked = false;
        UpdateGameSelection();
        SavePreferences();
    }

    private void UpdateGameSelection()
    {
        bool selected = GameTarget.CanSelect(preferences.GamePath);
        GameExecutableLabel.Text = selected ? Path.GetFileName(preferences.GamePath) : "Игра не выбрана";
        GameExecutableLabel.ToolTip = selected ? preferences.GamePath : null;
        ClearGameButton.IsEnabled = selected;
        BoostSelectedGame.IsEnabled = selected;
        GameProcessStats.Text = "CPU — · Память —";
        GameProcessStatus.Text = selected ? "Измерения начнутся при включении режима." : "Выберите EXE игры в настройках сессии.";
        UpdateHome();
    }

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
            if (HideOnClose()) { e.Cancel = true; return; }
            if (changingProfile) { e.Cancel = true; return; }
            StopMeasurements();
            StopMatch();
            try { performance.End(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Восстановление настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private async void QuickOptimizeClick(object sender, RoutedEventArgs e) => await ToggleProfile(ProfileMode.Background);
    private async void GamerModeClick(object sender, RoutedEventArgs e)
    {
        if (automaticGame is not null) suppressAutomatic = automaticGame;
        await ToggleProfile(ProfileMode.Gamer);
    }

    private async Task ToggleProfile(ProfileMode requested, bool automatic = false)
    {
        if (requested == ProfileMode.Gamer && !automatic) Sections.SelectedItem = GamerTab;
        if (demo)
        {
            if (requested == ProfileMode.Gamer) ShowGameDemo();
            Status.Text = "Деморежим · настройки Windows остаются прежними";
            return;
        }
        if (busy || changingProfile) return;
        IPAddress? probe = null;
        if (requested == ProfileMode.Gamer && activeProfile != requested && !GameTarget.TryProbe(ProbeIp.Text, out probe))
        {
            GamerSource.Text = "Введи IPv4 или IPv6. Оставь поле пустым для замера до роутера.";
            ProbeIp.Focus();
            return;
        }
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
                    StopMatch();
                    activeProfile = ProfileMode.Off;
                    automaticGame = null;
                    await Task.Run(performance.End);
                    Status.Text = "Сессия завершена";
                    GamerSummary.Text = "Игровая сессия завершена. На экране остались последние измерения.";
                }
                if (previous == requested) return;

                bool gaming = requested == ProfileMode.Gamer;
                bool close = gaming && CloseBackgroundApps.IsChecked == true;
                bool power = gaming && PreferPower.IsChecked == true;
                bool lower = !gaming || LowerGameBackground.IsChecked == true;
                var progress = new Progress<PerformanceProfile.Progress>(step =>
                {
                    if (!changingProfile) return;
                    ProfileProgressBar.IsIndeterminate = false;
                    ProfileProgressBar.Maximum = step.Total;
                    ProfileProgressBar.Value = step.Done;
                    ProfileProgressText.Text = $"{step.Done + 1} из {step.Total} · {step.Name}";
                });
                var result = await Task.Run(() => performance.Begin(lower, close, power, progress));
                if (!gaming && result.Lowered == 0)
                {
                    await Task.Run(performance.End);
                    Status.Text = "Подходящих фоновых процессов сейчас нет";
                    return;
                }
                activeProfile = requested;
                var summary = $"Понижен приоритет: {result.Lowered} · Запросы закрытия: {result.CloseRequests}";
                summary += result.Power switch
                {
                    PerformanceProfile.PowerResult.Changed => "\nВключён план «Высокая производительность»",
                    PerformanceProfile.PowerResult.AlreadyActive => "\nПлан «Высокая производительность» уже включён",
                    PerformanceProfile.PowerResult.Offline => "\nПитание от сети не подтверждено",
                    PerformanceProfile.PowerResult.Unavailable => "\nПлан недоступен, сохранён исходный",
                    _ => ""
                };
                if (result.Lowered == 0 && result.CloseRequests == 0) summary += "\nФоновые процессы остались без изменений";
                if (result.Skipped > 0) summary += $"\nПропущено из-за доступа или выхода: {result.Skipped}";
                Status.Text = (gaming ? "Игровая сессия активна" : "Фоновая оптимизация активна") + $" · Изменён приоритет: {result.Lowered}";
                if (!gaming) return;
                GamerSummary.Text = summary + "\n\nSteam, Discord, игры и WinDivert остаются активными.";
                GamerNetworkTitle.Text = probe is null ? "Задержка до роутера" : "Задержка до указанного IP";
                GamerSource.Text = "Начинаю измерения…";
                preferences = preferences with { ProbeAddress = ProbeIp.Text.Trim(), BoostGame = BoostSelectedGame.IsChecked == true };
                SavePreferences();
                automaticGame = automatic ? preferences.GamePath : null;
                absentChecks = 0;
                StartMatch(preferences.GamePath);
                _ = MeasureGame(probe, preferences.GamePath, preferences.BoostGame);
            }, "Применяю параметры фоновой нагрузки…");
        }
        finally
        {
            changingProfile = false;
            GameConfig.IsEnabled = activeProfile != ProfileMode.Gamer;
            ProfileProgress.Visibility = Visibility.Collapsed;
            OptimizeButton.Content = activeProfile == ProfileMode.Background ? "Отменить" : "Оптимизация";
            GamerButton.Content = activeProfile == ProfileMode.Gamer ? "Завершить сессию" : "Начать сессию";
            SessionButton.Content = activeProfile == ProfileMode.Gamer ? "Завершить" : "Начать";
        }
    }

    private async Task MeasureGame(IPAddress? probe, string game, bool boost)
    {
        using var cancellation = new CancellationTokenSource();
        gameMonitor = cancellation;
        var telemetry = new GameTelemetry(probe);
        var hasGame = GameTarget.CanSelect(game);
        (PerformanceProfile.GameReading Reading, long At)? previous = null;
        try
        {
            using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            do
            {
                var connection = telemetry.Measure(cancellation.Token);
                if (hasGame)
                {
                    var gameTask = Task.Run(() => (Reading: performance.ObserveGame(game, boost), At: Stopwatch.GetTimestamp()), cancellation.Token);
                    await Task.WhenAll(connection, gameTask);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var sample = await gameTask;
                    ShowGameProcess(sample.Reading, previous, sample.At);
                    previous = sample;
                    ObserveFrames(sample.Reading.Pid);
                }
                var snapshot = await connection;
                cancellation.Token.ThrowIfCancellationRequested();
                ShowGameSnapshot(snapshot, probe is not null);
                if (snapshot.History.LastOrDefault() is { } sampleNow) match?.AddSample(sampleNow.Cpu, sampleNow.Latency);
            }
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

    private void ShowGameProcess(PerformanceProfile.GameReading reading, (PerformanceProfile.GameReading Reading, long At)? previous, long now)
    {
        string cpu = "—";
        if (previous is { } last && last.Reading.Pid == reading.Pid && last.Reading.StartedTicks == reading.StartedTicks &&
            reading.CpuTime is { } used && last.Reading.CpuTime is { } before)
        {
            var seconds = Stopwatch.GetElapsedTime(last.At, now).TotalSeconds;
            if (seconds > 0) cpu = $"{Math.Clamp((used - before).TotalSeconds * 100 / seconds / Environment.ProcessorCount, 0, 100):0.#}%";
        }
        string memory = reading.Memory is { } bytes ? $"{bytes / 1048576.0:N0} МиБ" : "—";
        GameProcessStats.Text = $"CPU {cpu} · Память {memory}";
        GameProcessStatus.Text = reading.State switch
        {
            PerformanceProfile.GameState.Boosted => $"PID {reading.Pid} · Активен приоритет «Выше среднего»",
            PerformanceProfile.GameState.Restricted => "Windows ограничила доступ к измерениям или изменению приоритета.",
            PerformanceProfile.GameState.Observing => $"PID {reading.Pid} · Идёт замер ресурсов игры",
            PerformanceProfile.GameState.AlreadyConfigured => $"PID {reading.Pid} · Существующий приоритет сохранён",
            _ => "Ожидаю запуска выбранного EXE. Игру можно открыть после включения режима."
        };
    }

    private void ShowGameSnapshot(GameTelemetry.Snapshot snapshot, bool customAddress = false)
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
        GamerNote.Text = customAddress ? (snapshot.History.Length >= 5 && snapshot.Median is null
            ? "Этот адрес не ответил на ICMP. Пинг в самой игре может быть доступен."
            : "Это ICMP до указанного IP. Задержка игрового соединения может отличаться.")
            : !snapshot.IsMeasuringNetwork ? "На выбранном маршруте нельзя измерить задержку до шлюза. Загрузка CPU по-прежнему доступна."
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
        if (captureDemo) GameExecutableLabel.Text = "FieldNotes.exe · демо";
        GameProcessStats.Text = "CPU 14% · Память 1 632 МиБ";
        GameProcessStatus.Text = "Демо · Активен приоритет «Выше среднего»";
    }

    private void WindowsGameSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:gaming-gamemode");
    private void WindowsDownloadSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:delivery-optimization-advanced");
    private void WindowsGraphicsSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:display-advancedgraphics");

    private void OpenSettingsPage(string address)
    {
        try { using var launched = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Status.Text = ex.Message; }
    }
}
