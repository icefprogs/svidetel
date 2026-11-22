using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Svidetel;

public partial class MainWindow
{
    private const string HistoryFile = "match-history.json";
    private const string GoodRunsFile = "good-runs.json";
    private const string ConfigFile = "game-configs.json";
    private List<MatchResult> matchHistory = [];
    private Dictionary<string, DateTimeOffset> goodRuns = new(StringComparer.OrdinalIgnoreCase);
    private List<GameConfigCopy> configCopies = [];
    private bool loadingSteam, launchingSteam, matchingSteam, useExample;
    private GameEnvironment? sessionEnvironment;
    private string? appliedChanges;
    private SteamGame[] steamLibrary = [];
    private readonly Dictionary<uint, string> steamSlots = [];
    private sealed record HistoryRow(MatchResult Report, string Label);
    private sealed record GameConfigCopy(string Game, string Path, byte[] Bytes, DateTimeOffset At);

    private void LoadLibraryData()
    {
        matchHistory = engine.Read<List<MatchResult>>(HistoryFile) ?? [];
        if (lastMatch is { } previous && !matchHistory.Any(r => r.At == previous.At)) matchHistory.Insert(0, previous);
        goodRuns = new(engine.Read<Dictionary<string, DateTimeOffset>>(GoodRunsFile) ?? [], StringComparer.OrdinalIgnoreCase);
        configCopies = engine.Read<List<GameConfigCopy>>(ConfigFile) ?? [];
        if (engine.Read<Dictionary<uint, string>>("steam-games.json") is { } slots)
            foreach (var (id, path) in slots) steamSlots[id] = path;
        LoadNetworkRuns();
    }

    private async void SteamRefreshClick(object sender, RoutedEventArgs e) => await RefreshSteamLibrary();

    private async Task RefreshSteamLibrary()
    {
        if (loadingSteam || demo || captureDemo) return;
        loadingSteam = true;
        var current = (SteamGames.SelectedItem as SteamGame)?.Id;
        try
        {
            SteamStatus.Text = "Ищу установленные игры…";
            steamLibrary = await Task.Run(SteamLibrary.Read);
            SteamGames.ItemsSource = steamLibrary;
            SteamGames.SelectedItem = steamLibrary.FirstOrDefault(game => game.Id == current)
                ?? steamLibrary.FirstOrDefault(game => steamSlots.TryGetValue(game.Id, out var path) && SamePath(path, preferences.GamePath))
                ?? steamLibrary.FirstOrDefault();
            SteamStatus.Text = steamLibrary.Length == 0
                ? "Установленные игры Steam не найдены. Можно выбрать EXE вручную в «Сессии»."
                : $"Игры Steam · {steamLibrary.Length}. Выбери игру и укажи её EXE для записи.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            SteamStatus.Text = "Не удалось прочитать библиотеку Steam";
        }
        finally { loadingSteam = false; }
    }

    private void SteamGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameHistoryList is null) return;
        var game = SteamGames.SelectedItem as SteamGame;
        SteamStatus.Text = game is not null && steamSlots.TryGetValue(game.Id, out var executable)
            ? $"EXE для записи · {Path.GetFileName(executable)}"
            : "Выбери исполняемый файл игры для записи.";
        RefreshMatchList();
    }

    private void RefreshMatchList()
    {
        if (GameHistoryList is null) return;
        var active = SteamGames.SelectedItem is SteamGame game ? steamSlots.GetValueOrDefault(game.Id) : preferences.GamePath;
        var filtered = SteamGames.SelectedItem is null
            ? matchHistory.Where(report => string.IsNullOrEmpty(active) || SamePath(report.Game, active))
            : matchHistory.Where(report => active is not null && SamePath(report.Game, active));
        GameHistoryList.ItemsSource = filtered.Select(report => new HistoryRow(report,
            $"{report.At:g} · {Path.GetFileNameWithoutExtension(report.Game)} · {Stats(report)}")).ToArray();
    }

    private void SelectSteamExecutableClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || busy || activeProfile == ProfileMode.Gamer || SteamGames.SelectedItem is not SteamGame game) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = game.Name, InitialDirectory = game.Folder, Filter = "Игра (*.exe)|*.exe", CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (!GameTarget.CanSelect(picker.FileName) || !picker.FileName.StartsWith(
            game.Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            SteamStatus.Text = "Выбери EXE внутри папки установки этой игры.";
            return;
        }
        steamSlots[game.Id] = picker.FileName;
        TrySave("steam-games.json", steamSlots);
        SelectGameForSession(picker.FileName);
        SteamStatus.Text = $"EXE для записи · {Path.GetFileName(picker.FileName)}";
        RefreshMatchList();
    }

    private void SelectGameForSession(string path)
    {
        if (profiles.FirstOrDefault(profile => SamePath(profile.Executable, path)) is { } profile) ApplyProfile(profile);
        else
        {
            preferences = preferences with { GamePath = path, BoostGame = false };
            BoostSelectedGame.IsChecked = false;
            UpdateGameSelection();
        }
        UpdateProfilePicker();
        SavePreferences();
    }

    private void OpenSteamProfileClick(object sender, RoutedEventArgs e)
    {
        if (busy || demo || captureDemo || activeProfile != ProfileMode.Off || SteamGames.SelectedItem is not SteamGame game ||
            !steamSlots.TryGetValue(game.Id, out var path)) return;
        SelectGameForSession(path);
        Sections.SelectedItem = GamerTab;
    }

    private void StorageSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:storagesense");

    private async void LaunchSteamGameClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || busy || launchingSteam || activeProfile != ProfileMode.Off || SteamGames.SelectedItem is not SteamGame game) return;
        if (!steamSlots.TryGetValue(game.Id, out var executable) || !GameTarget.CanSelect(executable) || !File.Exists(executable))
        {
            if (preferences.AutoSteam)
            {
                nextSteamImport = DateTimeOffset.MinValue;
                if (OpenSteam($"steam://run/{game.Id}"))
                    SteamStatus.Text = "Ожидаю подтверждение запуска от Steam. Запись начнётся автоматически.";
            }
            else SteamStatus.Text = "Включите автозапись в параметрах или выберите EXE игры.";
            return;
        }
        SelectGameForSession(executable);
        if (!GameTarget.TryProbe(ProbeIp.Text, out var target))
        {
            SteamStatus.Text = "Укажи корректный IP в настройках сессии.";
            return;
        }
        var button = (Button)sender;
        launchingSteam = true;
        button.IsEnabled = false;
        try { await LaunchSteamSession(game, executable, target); }
        finally { launchingSteam = false; button.IsEnabled = true; }
    }

    private async Task LaunchSteamSession(SteamGame game, string executable, System.Net.IPAddress? target)
    {
        SteamStatus.Text = "Измеряю соединение перед запуском…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var checkAddress = target ?? (preferences.InternetProbe ? NetworkRoutes.Find()?.Reference : null);
        try { steamNetworkBefore = await MeasureConnection(checkAddress, 4, timeout.Token); }
        catch (OperationCanceledException) { steamNetworkBefore = null; }
        steamNetworkCompared = false;
        steamSeenSamples = 0;
        if (busy || demo || activeProfile != ProfileMode.Off ||
            !SamePath(preferences.GamePath, executable) || (SteamGames.SelectedItem as SteamGame)?.Id != game.Id ||
            !GameTarget.TryProbe(ProbeIp.Text, out var selectedTarget) || !Equals(target, selectedTarget))
        {
            steamNetworkBefore = null;
            SteamStatus.Text = "Выбор игры или состояние сессии изменились.";
            return;
        }
        await ToggleProfile(ProfileMode.Gamer, automatic: true);
        if (activeProfile != ProfileMode.Gamer) return;
        matchingSteam = true;
        if (!OpenSteam($"steam://run/{game.Id}"))
        {
            await ToggleProfile(ProfileMode.Gamer, automatic: true);
            matchingSteam = false;
            return;
        }
        SteamStatus.Text = "Запущено через Steam. Клиент ставит фоновые загрузки на паузу, если разрешение загрузок во время игры выключено.";
        GamerSummary.Text += "\nSteam управляет паузой загрузок по своим настройкам. ICMP измеряется до шлюза и выбранного узла.";
    }

    private bool OpenSteam(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status.Text = "Steam не смог открыть ссылку. Проверь установку клиента.";
            return false;
        }
    }

    private void SteamDownloadSettingsClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo) return;
        if (OpenSteam("steam://open/settings"))
            Status.Text = "Steam → Загрузки → выключи «Разрешить загрузки во время игры» для автоматической паузы.";
    }

    private async void PreflightClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || loadingSteam) return;
        var game = SteamGames.SelectedItem as SteamGame;
        var executable = preferences.GamePath;
        SteamStatus.Text = "Проверяю питание, диск и обновления…";
        try
        {
            var findings = await Task.Run(() =>
            {
                List<string> messages = [];
                if (SessionDiagnostics.OnBattery() == "Работа от батареи")
                    messages.Add("Ноутбук на батарее · подключи питание перед замером.");
                var path = game?.Folder ?? executable;
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < 10L * 1024 * 1024 * 1024)
                        messages.Add($"На диске {drive.Name} свободно {drive.AvailableFreeSpace / 1073741824.0:0.#} ГБ · проверь хранилище Windows.");
                }
                if (game is not null)
                {
                    if (SteamLibrary.DownloadRate() is > 0.1 and var rate)
                        messages.Add($"Steam зафиксировал загрузку · {rate:0.#} Мбит/с. Поставь её на паузу в очереди Steam.");
                    var pending = SteamLibrary.Read().Where(item => item.Updating).Select(item => item.Name).Take(4).ToArray();
                    if (pending.Length > 0)
                        messages.Add($"Steam пометил обновления активными · {string.Join(", ", pending)}. Проверь очередь загрузок Steam.");
                }
                return messages;
            });
            SteamStatus.Text = findings.Count == 0 ? "Питание, место и известные обновления выглядят нормально." : string.Join("\n", findings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { SteamStatus.Text = "Часть проверок перед игрой недоступна."; }
    }

    private void RememberMatch(MatchResult report)
    {
        matchHistory.Insert(0, report);
        if (matchHistory.Count > 30) matchHistory.RemoveRange(30, matchHistory.Count - 30);
        TrySave(HistoryFile, matchHistory);
        RefreshMatchList();
    }

    private void OpenHistoryMatchClick(object sender, RoutedEventArgs e)
    {
        if (GameHistoryList.SelectedItem is HistoryRow row) OpenHistoricalReport(row.Report);
    }

    private void OpenHistoricalReport(MatchResult report)
    {
        useExample = false;
        lastMatch = report;
        ShowReport();
        Sections.SelectedItem = ReviewTab;
    }

    private void MarkGoodMatchClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || GameHistoryList.SelectedItem is not HistoryRow row || row.Report.Game.Length == 0) return;
        goodRuns[row.Report.Game] = row.Report.At;
        TrySave(GoodRunsFile, goodRuns);
        GoodMatchDiff.Text = $"Удачный замер сохранён · {row.Report.At:g}.";
        ShowReport();
    }

    private string CompareGoodRun(MatchResult report)
    {
        if (!goodRuns.TryGetValue(report.Game, out var good)) return "";
        var previous = matchHistory.FirstOrDefault(item => item.At == good && SamePath(item.Game, report.Game));
        if (previous is null || previous.At == report.At || previous.Environment is not { } before || report.Environment is not { } after)
            return "";
        List<string> changes = [];
        Check("Версия игры", before.GameVersion, after.GameVersion);
        Check("Драйвер GPU", before.Driver, after.Driver);
        Check("План питания", before.Power, after.Power);
        if (before.ConfigHash is not null && after.ConfigHash is not null && before.ConfigHash != after.ConfigHash)
            changes.Add("Содержимое выбранного игрового конфига отличается.");
        return $"\nСравнение с удачной сессией от {good:g}\n" +
            (changes.Count == 0 ? "Проверенные версии и настройки совпадают либо данных недостаточно." : string.Join("\n", changes));

        void Check(string label, string? oldValue, string? newValue)
        {
            if (!string.IsNullOrEmpty(oldValue) && !string.IsNullOrEmpty(newValue) && oldValue != newValue)
                changes.Add($"{label} · {oldValue} → {newValue}");
        }
    }

    private void SaveGameConfigClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || !GameTarget.CanSelect(preferences.GamePath)) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери конфиг игры", Filter = "Файлы настроек|*.ini;*.json;*.cfg;*.toml;*.xml;*.yaml;*.yml",
            CheckFileExists = true, InitialDirectory = Path.GetDirectoryName(preferences.GamePath)
        };
        if (picker.ShowDialog(this) != true || !SafeConfig(picker.FileName)) return;
        try
        {
            var copy = new GameConfigCopy(preferences.GamePath, picker.FileName, ReadGameConfig(picker.FileName), DateTimeOffset.Now);
            var updated = configCopies.Prepend(copy).Take(20).ToList();
            engine.Save(ConfigFile, updated);
            configCopies = updated;
            ConfigStatus.Text = $"{Path.GetFileName(copy.Path)} · сохранено {copy.At:g}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ConfigStatus.Text = ex.Message; }
    }

    private async void RestoreGameConfigClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || activeProfile != ProfileMode.Off) return;
        var copies = configCopies.Where(item => SamePath(item.Game, preferences.GamePath)).ToArray();
        if (copies.Length == 0) { ConfigStatus.Text = "Для выбранной игры пока нет копий."; return; }
        var selected = ChooseItem("Копии конфигурации", copies, item => $"{item.At:g} · {Path.GetFileName(item.Path)}");
        if (selected is null || !SafeConfig(selected.Path)) return;
        if (await Task.Run(() => IsGameRunning(selected.Game))) { ConfigStatus.Text = "Сначала закрой игру."; return; }
        if (MessageBox.Show(this, $"Заменить файл {selected.Path} выбранной копией? Текущая версия будет сохранена.",
            "Восстановление конфига", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (await Task.Run(() => IsGameRunning(selected.Game)) || !SafeConfig(selected.Path)) return;
        var temporary = selected.Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var backup = selected with { Bytes = ReadGameConfig(selected.Path), At = DateTimeOffset.Now };
            var updated = configCopies.Prepend(backup).Take(20).ToList();
            engine.Save(ConfigFile, updated);
            configCopies = updated;
            File.WriteAllBytes(temporary, selected.Bytes);
            File.Replace(temporary, selected.Path, null);
            ConfigStatus.Text = $"Восстановлен {Path.GetFileName(selected.Path)}. Исходная версия сохранена.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ConfigStatus.Text = ex.Message; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadGameConfig(string path)
    {
        using var source = File.OpenRead(path);
        if (source.Length > 512_000) throw new IOException("Размер конфигурации превышает 500 КБ.");
        var bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        return bytes;
    }

    private static bool SafeConfig(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path.AsSpan(2).Contains(':') || !File.Exists(path)) return false;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".ini" or ".json" or ".cfg" or ".toml" or ".xml" or ".yaml" or ".yml")) return false;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
