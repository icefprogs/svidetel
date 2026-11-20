using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Svidetel;

public partial class MainWindow
{
    private const int FreezeHotkey = 0x521;
    private const string ReportFile = "last-match.json";
    private const string ComparisonFile = "comparison.json";
    private const string ProfilesFile = "game-profiles.json";
    private const string ServersFile = "servers.json";
    private MatchRecorder? match;
    private MatchResult? lastMatch;
    private RunComparison comparison = new();
    private List<GameProfile> profiles = [];
    private PresentMonCapture? frameCapture;
    private int framePid;
    private bool hotkeyRegistered, checkingProfiles, editingProfile;
    private string? automaticGame, suppressAutomatic;
    private int absentChecks;
    private ServerReading? suggestedServer;
    private HwndSource? hotkeySource;

    private void OpenGameTabClick(object sender, RoutedEventArgs e) => Sections.SelectedItem = GamerTab;
    private void OpenReviewClick(object sender, RoutedEventArgs e) => Sections.SelectedItem = ReviewTab;

    private void InitializeMatchDashboard()
    {
        try
        {
            lastMatch = engine.Read<MatchResult>(ReportFile);
            comparison = engine.Read<RunComparison>(ComparisonFile) ?? new();
            profiles = engine.Read<List<GameProfile>>(ProfilesFile) ?? [];
            ServerList.Text = engine.Read<string>(ServersFile) ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Text = "История сессий: " + ex.Message;
        }
        UpdateProfilePicker();
        ShowReport();
        UpdateHome();
        if (captureDemo || demo) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await CheckAutomaticProfile();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void StartMatch(string executable)
    {
        match = new(executable);
        framePid = 0;
        FrameStatus.Text = PresentMonCapture.IsPackaged ? "Кадры появятся при запуске выбранного EXE" : "В этой сборке нет PresentMon";
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        hotkeySource = HwndSource.FromHwnd(hwnd);
        hotkeySource?.AddHook(FreezeMessage);
        hotkeyRegistered = RegisterHotKey(hwnd, FreezeHotkey, 0x4006, 0x77);
        FreezeButton.IsEnabled = true;
        FreezeStatus.Text = hotkeyRegistered ? "Ctrl + Shift + F8 — отметить фриз" : "Горячая клавиша занята. Используй кнопку в окне.";
        UpdateHome();
    }

    private void StopMatch()
    {
        var recorder = match;
        if (recorder is null) return;
        match = null;
        frameCapture?.Dispose();
        frameCapture = null;
        if (hotkeySource is not null)
        {
            if (hotkeyRegistered) UnregisterHotKey(hotkeySource.Handle, FreezeHotkey);
            hotkeySource.RemoveHook(FreezeMessage);
            hotkeySource = null;
        }
        hotkeyRegistered = false;
        FreezeButton.IsEnabled = false;
        FreezeStatus.Text = "Запись завершена";
        lastMatch = recorder.Finish();
        TrySave(ReportFile, lastMatch);
        ShowReport();
        UpdateHome();
    }

    private void ObserveFrames(int pid)
    {
        if (framePid == pid || match is not { } recorder || !PresentMonCapture.IsPackaged) return;
        frameCapture?.Dispose();
        frameCapture = null;
        framePid = pid;
        if (pid <= 0) return;
        var capture = new PresentMonCapture();
        frameCapture = capture;
        FrameStatus.Text = "Ожидаю первые кадры…";
        capture.Start(pid, recorder, message => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(capture, frameCapture)) FrameStatus.Text = message;
        }));
    }

    private nint FreezeMessage(nint hwnd, int message, nint key, nint data, ref bool handled)
    {
        if (message == 0x0312 && key == FreezeHotkey) { MarkFreeze(); handled = true; }
        return 0;
    }

    private void FreezeClick(object sender, RoutedEventArgs e) => MarkFreeze();
    private void MarkFreeze()
    {
        if (match?.MarkFreeze() == true) FreezeStatus.Text = $"Фриз отмечен в {DateTime.Now:HH:mm:ss}. Окно ±8 с появится после сессии.";
    }

    private void SaveProfileClick(object sender, RoutedEventArgs e)
    {
        var profile = CurrentProfile();
        if (profile is null) { Status.Text = "Выбери EXE игры и укажи корректный IP или оставь его пустым."; return; }
        int index = profiles.FindIndex(p => SamePath(p.Executable, profile.Executable));
        if (index >= 0) profiles[index] = profile;
        else if (profiles.Count < 24) profiles.Add(profile);
        else { Status.Text = "Сохранено 24 профиля. Удали один перед добавлением нового."; return; }
        TrySave(ProfilesFile, profiles);
        UpdateProfilePicker();
        Status.Text = "Профиль игры сохранён";
    }

    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not GameProfile selected) return;
        profiles.Remove(selected);
        TrySave(ProfilesFile, profiles);
        UpdateProfilePicker();
    }

    private GameProfile? CurrentProfile()
    {
        if (!GameTarget.CanSelect(preferences.GamePath) || !GameTarget.TryProbe(ProbeIp.Text, out _)) return null;
        return new(preferences.GamePath, ProbeIp.Text.Trim(), BoostSelectedGame.IsChecked == true,
            LowerGameBackground.IsChecked == true, CloseBackgroundApps.IsChecked == true, PreferPower.IsChecked == true,
            AutoGameProfile.IsChecked == true);
    }

    private void UpdateProfilePicker()
    {
        editingProfile = true;
        ProfilePicker.ItemsSource = profiles.ToArray();
        ProfilePicker.SelectedItem = profiles.Find(p => SamePath(p.Executable, preferences.GamePath));
        AutoGameProfile.IsChecked = (ProfilePicker.SelectedItem as GameProfile)?.Automatic == true;
        editingProfile = false;
    }

    private void ProfilePickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!editingProfile && !busy && activeProfile != ProfileMode.Gamer && ProfilePicker.SelectedItem is GameProfile profile)
            ApplyProfile(profile);
    }

    private void ApplyProfile(GameProfile profile)
    {
        preferences = preferences with { GamePath = profile.Executable, ProbeAddress = profile.Probe, BoostGame = profile.Boost };
        ProbeIp.Text = profile.Probe;
        BoostSelectedGame.IsChecked = profile.Boost;
        LowerGameBackground.IsChecked = profile.Lower;
        CloseBackgroundApps.IsChecked = profile.Close;
        PreferPower.IsChecked = profile.Power;
        AutoGameProfile.IsChecked = profile.Automatic;
        UpdateGameSelection();
    }

    private async Task CheckAutomaticProfile()
    {
        if (checkingProfiles || busy || changingProfile || demo || !profiles.Any(p => p.Automatic) && automaticGame is null) return;
        checkingProfiles = true;
        try
        {
            if (suppressAutomatic is { } suppressed && !await Task.Run(() => IsGameRunning(suppressed))) suppressAutomatic = null;
            if (activeProfile == ProfileMode.Gamer && automaticGame is { } running)
            {
                absentChecks = await Task.Run(() => IsGameRunning(running)) ? 0 : absentChecks + 1;
                if (absentChecks >= 2) await ToggleProfile(ProfileMode.Gamer);
                return;
            }
            if (activeProfile != ProfileMode.Off) return;
            var candidates = profiles.Where(p => p.Automatic && !SamePath(p.Executable, suppressAutomatic)).ToArray();
            var detected = await Task.Run(() => candidates.FirstOrDefault(p => IsGameRunning(p.Executable)));
            if (detected is null || busy || changingProfile || activeProfile != ProfileMode.Off) return;
            ApplyProfile(detected);
            await ToggleProfile(ProfileMode.Gamer, automatic: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Status.Text = "Автопрофиль: " + ex.Message; }
        finally { checkingProfiles = false; }
    }

    private static bool IsGameRunning(string path)
    {
        if (!GameTarget.CanSelect(path)) return false;
        using var self = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != self.Id && process.SessionId == self.SessionId && SamePath(process.MainModule?.FileName, path))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void UpdateHome()
    {
        HomeGame.Text = GameTarget.CanSelect(preferences.GamePath) ? Path.GetFileNameWithoutExtension(preferences.GamePath) : "Выбери игру";
        HomeLatest.Text = lastMatch is { } report ? $"{report.At:g} · {Stats(report)}" : "Пока нет завершённых сессий";
    }

    private void PrepareExampleReport()
    {
        double[] frames = [10, 11, 12, 11, 13, 17, 62, 41, 22, 14, 11, 12, 10, 13, 11, 10, 11, 10];
        lastMatch = new(@"C:\Demo\FieldNotes.exe", new DateTimeOffset(2026, 11, 21, 19, 12, 0, TimeSpan.FromHours(5)),
            8200, 91.6, 39.7, 20.4, frames.Select((frame, i) =>
                new MatchPoint(6 + i, frame, 5 + frame / 5, 29 + (int)(frame / 2), (long)(7 + i % 4))).ToArray(), [13], false);
        UpdateHome();
        HomeGame.Text = "Field Notes · демо";
        DefenderTitle.Text = "Защита в реальном времени включена · демо";
        DefenderEvents.Text = "Здесь появятся обнаружения и действия Microsoft Defender.";
        ShowReport();
    }

    private static string Stats(MatchResult report) => report.Fps is { } fps
        ? $"{fps:0.#} FPS · 1% low {Format(report.Low1)} FPS · P99 {Format(report.P99)} мс · {report.Frames:N0} кадров"
        : report.Limited ? "Достигнут предел записи кадров" : $"{report.Points.Length} замеров · без кадров";
    private static string Format(double? value) => value is { } number ? number.ToString("0.#") : "—";

    private void ShowReport()
    {
        MatchStats.Text = lastMatch is { } report ? Stats(report) : "Заверши игровую сессию для просмотра результата.";
        MarksPicker.ItemsSource = lastMatch?.Marks.Select((x, i) => new { Index = i, Label = $"Отметка {i + 1} · {TimeSpan.FromSeconds(x):mm\\:ss}" }).ToArray();
        MarksPicker.SelectedIndex = lastMatch?.Marks.Length > 0 ? 0 : -1;
        BeforeButton.IsEnabled = AfterButton.IsEnabled = CanCompare(lastMatch);
        ComparisonText.Text = $"До · {(comparison.Before is { } a ? Stats(a) : "нет замера")}\n" +
            $"После · {(comparison.After is { } b ? Stats(b) : "нет замера")}";
        if (comparison.Before is { } before && comparison.After is { } after)
            ComparisonText.Text += SamePath(before.Game, after.Game)
                ? $"\nРазница · {after.Fps - before.Fps:+0.#;-0.#;0} FPS · 1% low {after.Low1 - before.Low1:+0.#;-0.#;0} FPS · P99 {after.P99 - before.P99:+0.#;-0.#;0} мс"
                : "\nСравнивай два прогона одной игры на одной сцене.";
        DrawMatch();
    }

    private static bool CanCompare(MatchResult? report) => report is { Frames: >= 100, Game.Length: > 0, Limited: false, Fps: not null };
    private void SaveBeforeClick(object sender, RoutedEventArgs e) => SaveComparison(before: true);
    private void SaveAfterClick(object sender, RoutedEventArgs e) => SaveComparison(before: false);
    private void SaveComparison(bool before)
    {
        if (!CanCompare(lastMatch)) return;
        comparison = before ? comparison with { Before = lastMatch } : comparison with { After = lastMatch };
        TrySave(ComparisonFile, comparison);
        ShowReport();
    }

    private void MarksChanged(object sender, SelectionChangedEventArgs e) => DrawMatch();
    private void DrawMatch()
    {
        if (FrameTimeline is null || lastMatch is not { } report) return;
        double end = report.Points.LastOrDefault()?.Second ?? 1;
        bool marked = MarksPicker.SelectedIndex >= 0 && MarksPicker.SelectedIndex < report.Marks.Length;
        double mark = marked ? report.Marks[MarksPicker.SelectedIndex] : 0;
        double start = marked ? Math.Max(0, mark - 8) : 0;
        end = marked ? Math.Max(start + 1, mark + 8) : Math.Max(1, end);
        var points = report.Points.Where(p => p.Second >= start && p.Second <= end).ToArray();
        FrameTimeline.Data = Trace(points, p => p.FrameMs, 33);
        GpuTimeline.Data = Trace(points, p => p.GpuMs, 33);
        CpuTimeline.Data = Trace(points, p => p.Cpu, 100);
        PingTimeline.Data = Trace(points, p => p.Ping, 100);
        var x = 700 * (mark - start) / (end - start);
        FreezeTimeline.Data = marked ? new LineGeometry(new(x, 4), new(x, 140)) : null;
        MatchRange.Text = points.Length == 0 ? "В выбранном интервале пока нет замеров."
            : $"{start:0.#}–{end:0.#} с · кадр пик {Format(points.Max(p => p.FrameMs))} мс · GPU кадра {Format(points.Max(p => p.GpuMs))} мс · CPU {points.Max(p => p.Cpu):0}% · пинг {points.Max(p => p.Ping)} мс";

        PathGeometry Trace(MatchPoint[] data, Func<MatchPoint, double?> get, double minimum)
        {
            var result = new PathGeometry();
            var scale = Math.Max(minimum, data.Select(get).Max() ?? 0);
            PathFigure? line = null;
            foreach (var point in data)
            {
                if (get(point) is not { } value) { line = null; continue; }
                var item = new Point(700 * (point.Second - start) / (end - start), 139 - 130 * value / scale);
                if (line is null) { line = new PathFigure { StartPoint = item }; result.Figures.Add(line); }
                line.Segments.Add(new LineSegment(item, true));
            }
            return result;
        }
    }

    private async void CompareServersClick(object sender, RoutedEventArgs e)
    {
        var addresses = new List<(string Label, System.Net.IPAddress Address)>();
        foreach (var line in ServerList.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || !GameTarget.TryProbe(parts[1], out var address) || address is null)
            { ServerResults.Text = "Каждая строка должна содержать «Название = IP»."; return; }
            if (!addresses.Any(e => e.Address.Equals(address))) addresses.Add((parts[0], address));
        }
        if (addresses.Count is < 1 or > 8) { ServerResults.Text = "Укажи до восьми адресов серверов."; return; }
        TrySave(ServersFile, ServerList.Text);
        CompareServers.IsEnabled = UseServer.IsEnabled = false;
        ServerResults.Text = "Измеряю…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var readings = (await ServerProbe.Compare(addresses, timeout.Token)).OrderByDescending(x => x.Replies).ThenBy(x => x.Median).ThenBy(x => x.Worst).ToArray();
            suggestedServer = readings.FirstOrDefault(x => x.Replies >= 3);
            ServerResults.Text = string.Join("\n", readings.Select(x => $"{x.Label} · {x.Replies}/4 ответов" +
                (x.Median.HasValue ? $" · медиана {x.Median} мс · максимум {x.Worst} мс" : " · ICMP без ответа")));
            if (suggestedServer is { } best) ServerResults.Text += $"\n\nПервый по стабильности и задержке: {best.Label}. Выбери его регион в самой игре.";
            UseServer.IsEnabled = suggestedServer is not null;
        }
        catch (Exception ex) { ServerResults.Text = "Проверка прервана: " + ex.Message; }
        finally { CompareServers.IsEnabled = true; }
    }

    private void UseServerClick(object sender, RoutedEventArgs e)
    {
        if (suggestedServer is not { } best) return;
        ProbeIp.Text = best.Address;
        preferences = preferences with { ProbeAddress = best.Address };
        SavePreferences();
        Status.Text = $"Адрес {best.Label} выбран для замера следующей сессии";
    }

    private void TrySave<T>(string file, T value)
    {
        if (captureDemo || demo) return;
        try { engine.Save(file, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status.Text = "Не удалось сохранить данные: " + ex.Message; }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
