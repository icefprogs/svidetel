using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Svidetel;

public partial class MainWindow
{
    private Forms.NotifyIcon? tray;
    private LaunchEvidence launchEvidence = new([], [], []);
    private bool preparingOptimization;

    private void LoadProcessOrigins()
    {
        try
        {
            launchEvidence = engine.Read<LaunchEvidence>("launch-evidence.json")
                ?? new(engine.Read<List<ProcessOrigin>>("process-origins.json") ?? [], [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Text = "Сохранённый журнал запуска не удалось прочитать.";
        }
    }
    private void SaveLaunches(LaunchEvidence evidence)
    {
        launchEvidence = evidence;
        engine.Save("launch-evidence.json", evidence);
    }
    private readonly bool captureDemo = Environment.GetCommandLineArgs().Contains("--capture-demo");

    private static void Capture(FrameworkElement content, string name)
    {
        content.Dispatcher.Invoke(content.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
        const int inset = 24;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth) + inset * 2,
            (int)Math.Ceiling(content.ActualHeight) + inset * 2, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.Resources["Canvas"], null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(inset, inset, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(Environment.CurrentDirectory, "docs", "screenshots");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        png.Save(file);
    }

    private void PrepareDemoCapture()
    {
        if (!captureDemo) return;
        ThemeChoice.SelectedIndex = 0;
        AccentMode.SelectedIndex = 0;
        CompactRows.IsChecked = false;
        MinimizeToTray.IsChecked = false;
        WatchEnabled.IsChecked = false;
        CloseBackgroundApps.IsChecked = false;
        PreferPower.IsChecked = false;
        LoadDemo();
        Loaded += (_, _) =>
        {
            PrepareExampleReport();
            Sections.SelectedItem = HomeTab;
            Capture((FrameworkElement)Content, "home.png");
            ToolsClick(ToolsButton, new RoutedEventArgs());
            Capture(ToolsButton.ContextMenu, "tools-menu.png");
            ToolsButton.ContextMenu.IsOpen = false;
            Sections.SelectedItem = ScanTab;
            Findings.SelectedIndex = 0;
            Capture((FrameworkElement)Content, "overview.png");
            OptimizeClick(this, new RoutedEventArgs());
            ShowGameDemo();
            Sections.SelectedItem = GamerTab;
            Capture((FrameworkElement)Content, "gamer.png");
            Sections.SelectedItem = ReviewTab;
            Capture((FrameworkElement)Content, "review.png");
            Sections.SelectedItem = LibraryTab;
            SteamGames.ItemsSource = new[] { "Field Notes · демо" };
            SteamGames.SelectedIndex = 0;
            GameHistoryList.ItemsSource = new[] { new { Label = "19:12 · Field Notes · 91,6 FPS · 1% low 39,7 FPS" } };
            SteamStatus.Text = "Демо · установленные игры появятся здесь после чтения библиотеки Steam.";
            Capture((FrameworkElement)Content, "games.png");
            Sections.SelectedItem = ServersTab;
            AutomaticNetworkStatus.Text = "Контрольные адреса отвечают";
            AutomaticNetworkDetail.Text = "Демонстрационные данные";
            NetworkAdapterInfo.Text = "Wi-Fi · подключено · линк 866 Мбит/с";
            AutomaticRouter.Text = "1 мс · пропуски 0/8";
            AutomaticRouterAddress.Text = "192.0.2.1";
            AutomaticInternet.Text = "14 мс · пропуски 0/8";
            AutomaticInternetAddress.Text = "Контрольный узел · 192.0.2.10";
            AutomaticTraffic.Text = "↓ 27,6  ↑ 0,4 Мбит/с";
            AutoNetworkComparison.Text = "Простой · 14 мс, 0/8 пропусков\nТрафик · 71 мс, 1/8 пропусков\nРазница медиан · +57 мс";
            NetworkTarget.Text = "192.0.2.10";
            idleNetwork = new("Demo", "192.0.2.1", "192.0.2.10", [1, 1, 2, 1, 1, 0, 2, 1], [14, 15, 12, 14, 14, 16, 13, 14], 927, 220);
            loadedNetwork = new("Demo", "192.0.2.1", "192.0.2.10", [1, 2, 2, 2, 1, 3, 2, 2], [55, 71, 88, 64, 71, 90, 72, null], 27_552, 455);
            ShowNetworkRuns();
            NetworkLabel.Text = "Домашняя сеть";
            Capture((FrameworkElement)Content, "network.png");
            Sections.SelectedItem = SettingsTab;
            Capture((FrameworkElement)Content, "settings.png");
            ThemeChoice.SelectedIndex = 1;
            Capture((FrameworkElement)Content, "settings-light.png");
            Close();
        };
    }

    private string Evidence(Finding item)
    {
        if (demo) return "";
        var advice = item.Kind switch
        {
            "task" => "Сверьте дату последнего запуска и действие в Планировщике заданий. Если приложение удалено, отключите его задачу в Планировщике.",
            "service" => "Проверьте приложение и зависимости службы в «Службах». Запись пустого пути не подтверждает, что службу можно удалить.",
            "startup" => "Если приложение удалено, проверьте свойства ярлыка и переместите его из папки автозагрузки.",
            _ => "Проверьте, есть ли приложение в установленных программах. Если оно нужно, восстановите установку или исправьте путь автозапуска."
        };
        var note = item.MissingTarget ? "\nЦелевой EXE отсутствует в доступной локальной папке. " + advice : "";
        if (item.Kind is "process" or "service" && item.ProcessId > 0)
        {
            var related = items.Where(other => other.ProcessId == item.ProcessId &&
                other.Kind == (item.Kind == "process" ? "service" : "process")).Select(other => other.Name);
            var serviceLinks = string.Join(", ", related);
            if (serviceLinks.Length > 0) note += $"\n\nСвязь службы и процесса по PID {item.ProcessId} в снимке: {serviceLinks}";
        }
        if (item.Kind == "task")
        {
            var taskPath = item.Location + item.Name;
            var events = launchEvidence.Tasks.Where(entry => string.Equals(entry.TaskPath, taskPath, StringComparison.OrdinalIgnoreCase))
                .Take(3).Select(entry => $"{entry.Time.ToLocalTime():g} · PID {entry.ProcessId} · запись {entry.RecordId}").ToList();
            if (events.Count > 0) note += "\n\nЖурнал планировщика · событие 129\n" + string.Join("\n", events);
        }
        if (item.ProcessStartedAt is { } started && !string.IsNullOrWhiteSpace(item.Executable))
        {
            var origins = launchEvidence.Processes.Where(entry => entry.ProcessId == item.ProcessId &&
                    Math.Abs((entry.Time - started).TotalSeconds) < 3 &&
                    string.Equals(entry.Executable, item.Executable, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(entry => entry.Source);
            foreach (var origin in origins)
                note += $"\n\n{origin.Source} · запись {origin.RecordId}\nЗапуск {origin.Time.ToLocalTime():g}\nСоздавшее приложение: {origin.ParentExecutable}";
            var scheduled = launchEvidence.Tasks.Where(entry => entry.ProcessId == item.ProcessId &&
                Math.Abs((entry.Time - started).TotalSeconds) < 3).Select(entry => entry.TaskPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (scheduled.Count == 1)
                note += $"\n\nПланировщик · те же PID и время запуска\n{scheduled[0]}";
        }
        if (string.IsNullOrWhiteSpace(item.Executable)) return note;
        var matches = items.Where(other => other.Id != item.Id &&
            (item.Kind == "process" ? other.Kind is "run" or "startup" or "service" or "task" or "policy" : other.Kind == "process") &&
            string.Equals(other.Executable, item.Executable, StringComparison.OrdinalIgnoreCase))
            .Select(other => $"{other.Category}: {other.Name} ({other.Location})").Take(8).ToArray();
        return matches.Length == 0 ? note : note + "\n\nСовпадает путь EXE (возможная связь)\n" + string.Join("\n", matches);
    }

    private void TrayClick(object sender, RoutedEventArgs e)
    {
        Tray().Visible = true;
        Hide();
    }

    private Forms.NotifyIcon Tray()
    {
        if (tray != null) return tray;
        var menu = new Forms.ContextMenuStrip();
        void Open() => Dispatcher.Invoke(OpenFromTray);
        menu.Items.Add("Открыть «Свидетель»", null, (_, _) => Open());
        var mark = menu.Items.Add("Отметить фриз", null, (_, _) => Dispatcher.Invoke(MarkFreeze));
        var report = menu.Items.Add("Последний разбор", null, (_, _) => Dispatcher.Invoke(() =>
        {
            OpenFromTray();
            if (matchHistory.FirstOrDefault() is { } latest) OpenHistoricalReport(latest);
        }));
        var scan = menu.Items.Add("Сканировать ПК");
        scan.Click += async (_, _) =>
        {
            Open();
            await ScanSystem();
        };
        menu.Opening += (_, _) =>
        {
            scan.Enabled = !busy;
            mark.Enabled = match is not null;
            report.Enabled = matchHistory.Count > 0;
        };
        menu.Items.Add("Выйти", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Witness.ico")).Stream;
        using var source = new System.Drawing.Icon(stream);
        var icon = (System.Drawing.Icon)source.Clone();
        tray = new Forms.NotifyIcon { Icon = icon, Text = "Свидетель", ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        tray.BalloonTipClicked += (_, _) =>
        {
            Open();
            if (openLastReportFromNotification && matchHistory.FirstOrDefault() is { } latest) OpenHistoricalReport(latest);
            openLastReportFromNotification = false;
        };
        Closed += (_, _) => { tray.Dispose(); menu.Dispose(); icon.Dispose(); };
        return tray;
    }

    private void NotifyReturn(string message)
    {
        if (QueueGameAlert(message)) return;
        var icon = Tray();
        openLastReportFromNotification = false;
        icon.Visible = true;
        icon.ShowBalloonTip(5000, "Свидетель · автозапуск вернулся", message, Forms.ToolTipIcon.Warning);
    }

    private async void OriginsClick(object sender, RoutedEventArgs e)
    {
        if (demo)
        {
            Status.Text = "Журнал запуска доступен после выхода из деморежима. Нажмите «Сканировать ПК».";
            return;
        }
        await Work(async () =>
        {
            SaveLaunches(await engine.Call<LaunchEvidence>(new { op = "launch-evidence" }));
            if (Findings.SelectedItem is Finding item) ShowFinding(item);
            Status.Text = $"События за сутки · процессы: {launchEvidence.Processes.Count} · задачи: {launchEvidence.Tasks.Count} · недоступные журналы: {launchEvidence.Errors.Count}";
            if (launchEvidence.Errors.Count > 0) Coverage.Text += "\n\nЖурналы при обновлении\n" + string.Join("\n", launchEvidence.Errors);
        }, "Читаю журналы запуска Windows…");
    }

    private async void OptimizeClick(object sender, RoutedEventArgs e)
    {
        if (busy || preparingOptimization) return;
        preparingOptimization = true;
        try { if (!demo && !await ScanSystem()) return; }
        finally { preparingOptimization = false; }
        var candidates = items.Where(x => x.CanDisable && !x.MissingTarget &&
            (x.Kind is "run" or "task" or "startup" ||
             x.Kind == "service" && x.State.EndsWith(" / Auto", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();
        var processCandidates = demo
            ? items.Where(x => x.Kind == "process").ToList()
            : items.Where(x => x.Kind == "process" && !items.Any(s => s.Kind == "service" && s.ProcessId == x.ProcessId))
                .Where(InteractiveProcess.CanRequestClose).OrderBy(x => x.Name).ToList();

        var panel = new DockPanel { Margin = new Thickness(24) };
        var dialog = new Window
        {
            Owner = this, Title = "Оптимизация", Width = 720, Height = demo ? 460 : 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        dialog.SetResourceReference(BackgroundProperty, "Canvas");
        dialog.SetResourceReference(ForegroundProperty, "Ink");
        dialog.SourceInitialized += (_, _) => ApplyWindowTone(dialog);
        var heading = new TextBlock { Text = "Оптимизация", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var intro = new TextBlock
        {
            Text = demo ? "Демоданные. Изменения недоступны."
                : "Выберите, что отключить при следующем запуске и какие открытые приложения закрыть сейчас. Сохраните работу перед закрытием.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), FontSize = 15
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        var progress = new ProgressBar { Height = 5, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        progress.SetResourceReference(ForegroundProperty, "Signal");
        progress.SetResourceReference(BackgroundProperty, "Control");
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        progressText.SetResourceReference(ForegroundProperty, "Muted");
        footer.Children.Add(progress);
        footer.Children.Add(progressText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(actions);
        var apply = new Button { Content = "Применить выбранное", IsEnabled = false };
        apply.SetResourceReference(Button.BackgroundProperty, "Accent");
        apply.SetResourceReference(Button.ForegroundProperty, "OnAccent");
        actions.Children.Add(apply);
        var done = new Button { Content = "Отмена", IsCancel = true };
        actions.Children.Add(done);
        var list = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selected = new List<Finding>();
        void AddGroup(string title, string empty, IReadOnlyList<Finding> options)
        {
            list.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 12) });
            if (options.Count == 0) list.Children.Add(new TextBlock { Text = empty, Opacity = .72, Margin = new Thickness(0, 0, 0, 12) });
            foreach (var item in options)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = $"{item.Name} · {item.Category}", FontWeight = FontWeights.SemiBold });
                var detail = item.Kind == "process" ? item.Executable : item.Command;
                content.Children.Add(new TextBlock { Text = item.MissingTarget ? "Целевой файл отсутствует. Проверьте исходную программу." : detail, TextWrapping = TextWrapping.Wrap, Opacity = .72 });
                var box = new CheckBox { Content = content, IsEnabled = !demo, Margin = new Thickness(0, 0, 0, 12) };
                box.Checked += (_, _) => { selected.Add(item); apply.IsEnabled = true; };
                box.Unchecked += (_, _) => { selected.Remove(item); apply.IsEnabled = selected.Count != 0; };
                list.Children.Add(box);
            }
        }
        AddGroup("Отключить будущий запуск", "Доступных записей нет", candidates);
        AddGroup("Закрыть приложение сейчас", "Подходящих открытых приложений нет", processCandidates);
        var applying = false;
        dialog.Closing += (_, e) => e.Cancel = applying;
        apply.Click += async (_, _) =>
        {
            if (applying || demo || selected.Count == 0) return;
            applying = true;
            apply.IsEnabled = done.IsEnabled = list.IsEnabled = false;
            progressText.Visibility = progress.Visibility = Visibility.Visible;
            progress.Maximum = selected.Count;
            try
            {
                await Work(async () =>
                {
                    var failures = new List<string>();
                    int disabled = 0, closed = 0, waiting = 0;
                    for (var i = 0; i < selected.Count; i++)
                    {
                        var item = selected[i];
                        progressText.Text = $"{i + 1} из {selected.Count} · {item.Name}";
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        try
                        {
                            if (item.Kind == "process")
                            {
                                if (await InteractiveProcess.RequestClose(item)) closed++;
                                else waiting++;
                            }
                            else { await engine.Disable(item); disabled++; }
                        }
                        catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
                        finally { progress.Value = i + 1; }
                    }
                    Status.Text = $"Отключён автозапуск: {disabled} · Закрыто: {closed} · Ждут завершения: {waiting} · Ошибки: {failures.Count}";
                    progressText.Text = Status.Text;
                    progressText.ToolTip = failures.Count > 0 ? string.Join("\n", failures) : null;
                }, "Применяю выбранные действия…");
            }
            finally
            {
                applying = false;
                done.Content = "Готово";
                done.IsEnabled = true;
                apply.Visibility = Visibility.Collapsed;
            }
        };
        if (captureDemo)
            dialog.Loaded += (_, _) =>
            {
                progress.Maximum = 3;
                progress.Value = 2;
                progressText.Text = "Демо · 3 из 3 · Field Notes";
                progressText.Visibility = progress.Visibility = Visibility.Visible;
                Capture((FrameworkElement)dialog.Content, "optimization.png");
                dialog.Close();
            };
        dialog.ShowDialog();
    }
}
