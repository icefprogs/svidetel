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
    private RunningSteamGame? recordingDiscoveredGame;
    private bool HasSelectedGame => GameTarget.CanSelect(preferences.GamePath) && File.Exists(preferences.GamePath);

    private void ChooseGameClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Выберите исполняемый файл игры", Filter = "Игра (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!GameTarget.CanSelect(picker.FileName))
        {
            GameProcessStatus.Text = "Этот файл нельзя выбрать. Укажите EXE игры на локальном диске.";
            return;
        }
        SelectGameForSession(picker.FileName);
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
        bool selected = HasSelectedGame;
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
        if (!demo && !captureDemo && activeProfile != ProfileMode.Gamer && !HasSelectedGame)
        {
            ChooseGameClick(sender, e);
            return;
        }
        if (automaticGame is not null) suppressAutomatic = automaticGame;
        await ToggleProfile(ProfileMode.Gamer);
    }

    private void UpdateSessionControls()
    {
        bool recording = activeProfile == ProfileMode.Gamer;
        GamerButton.Content = SessionButton.Content = recording ? "Завершить сессию"
            : HasSelectedGame ? "Начать запись" : "Выбрать игру";
        OptimizeButton.Header = activeProfile == ProfileMode.Background ? "Восстановить приоритет фона" : "Снизить приоритет фоновых приложений";
        HomeSteamButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        bool tuning = BoostSelectedGame.IsChecked == true || LowerGameBackground.IsChecked == true ||
            PreferPower.IsChecked == true || CloseBackgroundApps.IsChecked == true;
        HomeSessionState.Text = recording
            ? (recordingDiscoveredGame is null ? "Запись идёт" : "Автозапись Steam идёт") + " · Ctrl+Shift+F8 отмечает фриз"
            : HasSelectedGame ? (tuning ? "Готово к записи · временные изменения включены" : "Готово к записи")
            : preferences.AutoSteam ? "Запись начнётся при запуске обнаруженной игры Steam" : "Выберите EXE или игру из библиотеки Steam";
    }

    private async Task ToggleProfile(ProfileMode requested, bool automatic = false, bool compareBackground = false,
        RunningSteamGame? discoveredGame = null)
    {
        if (requested == ProfileMode.Gamer && !automatic) Sections.SelectedItem = GamerTab;
        if (demo)
        {
            if (requested == ProfileMode.Gamer) ShowGameDemo();
            Status.Text = "Деморежим · настройки Windows остаются прежними";
            return;
        }
        if (busy || changingProfile) return;
        var executable = discoveredGame?.Executable ?? preferences.GamePath;
        if (requested == ProfileMode.Gamer && activeProfile != ProfileMode.Gamer &&
            (!GameTarget.CanSelect(executable) || !File.Exists(executable)))
        {
            Status.Text = "Выберите доступный EXE игры.";
            return;
        }
        IPAddress? probe = null;
        if (requested == ProfileMode.Gamer && activeProfile != requested && discoveredGame is null &&
            !GameTarget.TryProbe(ProbeIp.Text, out probe))
        {
            GamerSource.Text = "Укажите корректный IPv4 или IPv6 либо оставьте поле пустым для автовыбора.";
            GameConfig.IsExpanded = true;
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
                    recordingDiscoveredGame = null;
                    await Task.Run(performance.End);
                    Status.Text = "Сессия завершена";
                    GamerSummary.Text = "Игровая сессия завершена. На экране остались последние измерения.";
                }
                if (previous == requested) return;

                bool gaming = requested == ProfileMode.Gamer;
                bool profileChanges = gaming && !compareBackground && discoveredGame is null;
                bool close = profileChanges && CloseBackgroundApps.IsChecked == true;
                bool power = profileChanges && PreferPower.IsChecked == true;
                bool lower = !gaming || compareBackground || (profileChanges && LowerGameBackground.IsChecked == true);
                bool boost = profileChanges && BoostSelectedGame.IsChecked == true;
                var progress = new Progress<PerformanceProfile.Progress>(step =>
                {
                    if (!changingProfile) return;
                    ProfileProgressBar.IsIndeterminate = false;
                    ProfileProgressBar.Maximum = step.Total;
                    ProfileProgressBar.Value = step.Done;
                    ProfileProgressText.Text = $"{step.Done + 1} из {step.Total} · {step.Name}";
                });
                var result = await Task.Run(() => performance.Begin(lower, close, power, progress));
                if ((!gaming || compareBackground) && result.Lowered == 0)
                {
                    await Task.Run(performance.End);
                    Status.Text = result.Skipped > 0 ? $"Приоритет не изменился · Недоступных процессов {result.Skipped}"
                        : "Подходящих фоновых процессов сейчас нет";
                    if (compareBackground) Sections.SelectedItem = ReviewTab;
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
                if (result.Details.Length > 0) summary += "\n" + string.Join("\n", result.Details);
                Status.Text = (gaming ? "Игровая сессия активна" : "Фоновая оптимизация активна") + $" · Изменён приоритет: {result.Lowered}";
                if (!gaming) return;
                GamerSummary.Text = summary + "\n\nSteam, Discord, игры и WinDivert остаются активными.";
                GamerNetworkTitle.Text = "Задержка соединения";
                GamerSource.Text = "Начинаю измерения…";
                if (discoveredGame is null)
                {
                    preferences = preferences with { ProbeAddress = ProbeIp.Text.Trim(), BoostGame = BoostSelectedGame.IsChecked == true };
                    SavePreferences();
                }
                automaticGame = automatic ? executable : null;
                recordingDiscoveredGame = discoveredGame;
                if (discoveredGame is not null)
                {
                    GameExecutableLabel.Text = discoveredGame.Game.Name;
                    GameExecutableLabel.ToolTip = executable;
                    GamerSummary.Text = "Автозапись Steam · только измерения";
                }
                StartMatch(executable);
                if (discoveredGame is not null) match?.AddEvent($"Steam подтвердил запуск · {discoveredGame.Game.Name}");
                appliedChanges = summary;
                var config = configCopies.FirstOrDefault(copy => SamePath(copy.Game, executable))?.Path;
                sessionEnvironment = await Task.Run(() => SessionDiagnostics.EnvironmentFor(executable, config));
                _ = MeasureGame(probe, executable, boost);
            }, "Применяю параметры фоновой нагрузки…");
        }
        finally
        {
            changingProfile = false;
            GameConfig.IsEnabled = activeProfile != ProfileMode.Gamer;
            ProfileProgress.Visibility = Visibility.Collapsed;
            if (activeProfile == ProfileMode.Gamer) UpdateSessionControls();
            else UpdateGameSelection();
            CompareOptimizedButton.IsEnabled = !useExample && CanCompare(lastMatch) && activeProfile == ProfileMode.Off;
        }
    }

    private async Task MeasureGame(IPAddress? probe, string game, bool boost)
    {
        using var cancellation = new CancellationTokenSource();
        gameMonitor = cancellation;
        bool externalProbe = preferences.InternetProbe;
        var telemetry = new GameTelemetry(probe, externalProbe);
        var diagnostics = new SessionDiagnostics();
        var hasGame = GameTarget.CanSelect(game);
        var observedRoute = Volatile.Read(ref networkChange);
        var watchStarted = Stopwatch.GetTimestamp();
        int diagnosticTurn = 0, missingGame = 0;
        bool sawGame = false;
        bool? steamDownloadActive = null;
        uint? controllers = null;
        string? previousPower = null, previousAdapter = null;
        (PerformanceProfile.GameReading Reading, long At)? previous = null;
        try
        {
            using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            do
            {
                if (externalProbe != preferences.InternetProbe)
                {
                    externalProbe = preferences.InternetProbe;
                    telemetry = new GameTelemetry(probe, externalProbe);
                }
                int epoch = Volatile.Read(ref networkChange);
                var refresh = epoch != observedRoute;
                observedRoute = epoch;
                var connection = Task.Run(() => telemetry.Measure(cancellation.Token, refresh), cancellation.Token);
                (ProcessLoad[] Top, string[] Started)? detail = null;
                double? steamRate = null;
                long? gameMemory = null;
                if (hasGame)
                {
                    var gameTask = Task.Run(() => (Reading: performance.ObserveGame(game, boost), At: Stopwatch.GetTimestamp()), cancellation.Token);
                    await Task.WhenAll(connection, gameTask);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var sample = await gameTask;
                    ShowGameProcess(sample.Reading, previous, sample.At);
                    sampleGamePid = sample.Reading.Pid;
                    gameMemory = sample.Reading.Memory;
                    if (sampleGamePid > 0 && previous?.Reading.Pid != sampleGamePid)
                        match?.AddEvent("Обнаружен процесс игры");
                    if (sampleGamePid > 0) { sawGame = true; missingGame = 0; }
                    else if (sawGame) missingGame++;
                    previous = sample;
                    ObserveFrames(sample.Reading.Pid);
                    if (diagnosticTurn++ % 2 == 0)
                    {
                        var readSteam = diagnosticTurn % 4 == 1;
                        var observed = await Task.Run(() => (Detail: diagnostics.Observe(sample.Reading.Pid),
                            SteamRate: readSteam ? SteamLibrary.DownloadRate() : null), cancellation.Token);
                        detail = observed.Detail;
                        steamRate = observed.SteamRate;
                    }
                }
                var snapshot = await connection;
                cancellation.Token.ThrowIfCancellationRequested();
                GamerNetworkTitle.Text = snapshot.Connection?.Target is not null ? "Задержка до контрольного узла" : "Задержка до роутера";
                ShowGameSnapshot(snapshot, snapshot.Connection?.Target is not null || probe is not null);
                ReadGameNetwork(snapshot);
                if (snapshot.History.LastOrDefault() is { } sampleNow)
                {
                    var power = SessionDiagnostics.OnBattery();
                    if (previousPower is not null && power is not null && previousPower != power) match?.AddEvent(power);
                    if (previousAdapter is not null && previousAdapter != sampleNow.Adapter) match?.AddEvent("Изменился сетевой адаптер");
                    previousPower = power;
                    previousAdapter = sampleNow.Adapter;
                    if (detail is { Started.Length: > 0 } load) match?.AddEvent("Новые фоновые процессы · " + string.Join(", ", load.Started));
                    var pads = SessionDiagnostics.ConnectedControllers();
                    if (controllers.HasValue && pads.HasValue && controllers != pads) match?.AddEvent("Изменилось подключение контроллера XInput");
                    controllers = pads;
                    if (steamRate is { } rate && steamDownloadActive != (rate > 0.1))
                    {
                        if (rate > 0.1) match?.AddEvent($"Steam сообщил о загрузке · {rate:0.#} Мбит/с");
                        else if (steamDownloadActive == true) match?.AddEvent("Steam сообщил об остановке загрузки");
                        steamDownloadActive = rate > 0.1;
                    }
                    match?.AddSample(sampleNow.Cpu, sampleNow.Latency, sampleNow.Router, sampleNow.RemoteProbed, sampleNow.RouterProbed,
                        gameMemory is { } memory ? (int)Math.Min(int.MaxValue, memory / 1048576) : null,
                        detail?.Top, sampleNow.ReceiveKbps, sampleNow.SendKbps, detail.HasValue ? SessionDiagnostics.CpuFrequency() : null);
                }
                UpdateOverlay();
                if (matchingSteam && sampleGamePid > 0) ShowSteamNetworkComparison(snapshot);
                if (missingGame >= 2 && !busy && !changingProfile)
                {
                    match?.AddEvent("Процесс игры завершился");
                    await ToggleProfile(ProfileMode.Gamer, automatic: true);
                    if (lastMatch is { } finished) _ = NotifyIfGameCrashed(finished);
                    return;
                }
                if (matchingSteam && !sawGame && Stopwatch.GetElapsedTime(watchStarted) >= TimeSpan.FromMinutes(3) && !busy)
                {
                    await ToggleProfile(ProfileMode.Gamer, automatic: true);
                    SteamStatus.Text = "Выбранный EXE не обнаружен за три минуты. Проверь, какой файл запускает игра.";
                    return;
                }
                if (!matchingSteam && automaticGame is not null && !sawGame &&
                    Stopwatch.GetElapsedTime(watchStarted) >= TimeSpan.FromSeconds(12) && !busy)
                {
                    await ToggleProfile(ProfileMode.Gamer, automatic: true);
                    return;
                }
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
        sampleGamePid = 0;
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
