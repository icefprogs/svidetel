using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Svidetel;

public partial class MainWindow
{
    private bool recordAfterOptimization;
    private MatchResult? crashReportOwner;
    private string? reportedCrash;

    private void ShowExampleClick(object sender, RoutedEventArgs e)
    {
        if (activeProfile != ProfileMode.Off) return;
        useExample = true;
        PrepareExampleReport();
        Sections.SelectedItem = ReviewTab;
    }

    private async void OptimizeAndCompareClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || useExample || busy || changingProfile ||
            activeProfile != ProfileMode.Off || lastMatch is not { } before || !CanCompare(before)) return;
        if (!File.Exists(before.Game)) { Status.Text = "EXE из первого замера больше недоступен."; return; }
        SelectGameForSession(before.Game);
        await ToggleProfile(ProfileMode.Gamer, compareBackground: true);
        recordAfterOptimization = activeProfile == ProfileMode.Gamer;
        if (recordAfterOptimization)
        {
            comparison = new(before);
            TrySave(ComparisonFile, comparison);
            FreezeStatus.Text = "Прогони ту же сцену. После завершения сессия сохранится как «После».";
        }
    }

    private static string LongSession(MatchPoint[] points)
    {
        if (points.Length < 24 || points[^1].Second - points[0].Second < 60) return "";
        int count = Math.Min(30, points.Length / 3);
        var first = points.Take(count).ToArray();
        var last = points.TakeLast(count).ToArray();
        var startFrequency = first.Select(point => (double?)point.CpuMHz).Average();
        var endFrequency = last.Select(point => (double?)point.CpuMHz).Average();
        return $"\nНачало → конец · пиковое время кадра {Format(first.Max(p => p.FrameMs))} → {Format(last.Max(p => p.FrameMs))} мс" +
            $" · память игры {Format(first.Max(p => p.GameMemoryMb))} → {Format(last.Max(p => p.GameMemoryMb))} МБ" +
            (startFrequency.HasValue && endFrequency.HasValue
                ? $" · частота CPU по Windows {Format(startFrequency)} → {Format(endFrequency)} МГц" : "");
    }

    private void ShowMatchDiagnostics(MatchResult report, MatchPoint[] points, double start, double end)
    {
        MatchWindowsEvents.Text = ReferenceEquals(crashReportOwner, report) ? reportedCrash : "";
        if (points.Length == 0) { MatchDiagnostics.Text = ""; return; }
        var top = points.SelectMany(point => point.Background ?? [])
            .GroupBy(load => load.Name).Select(group => new ProcessLoad(group.Key, group.Max(load => load.Cpu), group.Max(load => load.IoKbps)))
            .OrderByDescending(load => load.Cpu).Take(5).ToArray();
        var io = points.SelectMany(point => point.Background ?? []).MaxBy(load => load.IoKbps);
        var messages = new List<string>();
        if (useExample) messages.Add("Демонстрационная запись · значения приведены для примера.");
        messages.Add($"Роутер · {DescribeReplies(points.Where(point => point.RouterProbed).Select(point => point.Router).ToArray())}" +
            $" · узел · {DescribeReplies(points.Where(point => point.RemoteProbed).Select(point => point.Ping).ToArray())}");
        var peak = points.MaxBy(point => point.FrameMs);
        if (peak is { FrameMs: > 0, GpuMs: > 0 })
            messages.Add($"На пике кадр занял {Format(peak.FrameMs)} мс · работа GPU в этом интервале {Format(peak.GpuMs)} мс");
        if (top.Length > 0)
        {
            messages.Add("Пиковая нагрузка CPU фона в этом интервале · " +
                string.Join(", ", top.Select(process => $"{process.Name} {process.Cpu:0.#}%")));
            if (io is { IoKbps: > 0 }) messages.Add($"Больше всего ввода/вывода · {io.Name} {io.IoKbps} КиБ/с");
        }
        var rush = points.Max(point => point.ReceiveKbps);
        if (rush.HasValue) messages.Add($"Сетевой адаптер · пик приёма {rush} кбит/с");
        var events = report.Events?.Where(item => start <= item.Second && item.Second <= end).TakeLast(8).ToArray() ?? [];
        if (events.Length > 0)
            messages.Add("События в интервале\n" + string.Join("\n", events.Select(item => $"{item.Second:0.#} с · {item.Text}")));
        if (!useExample) messages.Add(LongSession(report.Points) + CompareGoodRun(report));
        MatchDiagnostics.Text = string.Join("\n", messages.Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    private async void ReviewCrashClick(object sender, RoutedEventArgs e)
    {
        if (lastMatch is not { } report || useExample || demo || captureDemo) return;
        MatchWindowsEvents.Text = "Читаю события Windows около завершения игры…";
        var events = await Task.Run(() => SessionDiagnostics.WindowsEvents(report));
        if (!ReferenceEquals(lastMatch, report)) return;
        crashReportOwner = report;
        reportedCrash = events.Length == 0
            ? "События сбоя игры или сброса видеодрайвера рядом с завершением не найдены."
            : string.Join("\n", events);
        MatchWindowsEvents.Text = reportedCrash;
    }

    private async Task NotifyIfGameCrashed(MatchResult report)
    {
        await Task.Delay(3000);
        if (!IsLoaded || !ReferenceEquals(lastMatch, report)) return;
        var events = await Task.Run(() => SessionDiagnostics.WindowsEvents(report));
        if (!IsLoaded || !ReferenceEquals(lastMatch, report) || !events.Any(item => item.EndsWith("событие 1000") || item.EndsWith("событие 1002")))
            return;
        crashReportOwner = report;
        reportedCrash = string.Join("\n", events);
        MatchWindowsEvents.Text = reportedCrash;
        var icon = Tray();
        icon.Visible = true;
        openLastReportFromNotification = true;
        icon.ShowBalloonTip(5000, "Сбой игры зафиксирован Windows", "Открой разбор сессии и журнал событий.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ExportMatchImageClick(object sender, RoutedEventArgs e)
    {
        if (lastMatch is not { } report || report.Points.Length == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "Svidetel-match.png", Filter = "PNG (*.png)|*.png", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(25, 26, 34)), null, new Rect(0, 0, 960, 400), 18, 18);
            Write(drawing, useExample ? "Свидетель · пример фриза" : "Свидетель · игровая сессия", 30, 24, 26, Brushes.White);
            Write(drawing, Stats(report), 30, 74, 17, Brushes.Gainsboro);
            var graph = new Rect(30, 121, 900, 204);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 38, 51)), null, graph);
            var mark = (MarksPicker.SelectedItem as FreezeSelection)?.Second;
            var start = mark is { } time ? Math.Max(0, time - 8) : report.Points[0].Second;
            var end = Math.Max(start + 1, mark is { } center ? center + 8 : report.Points[^1].Second);
            var points = report.Points.Where(point => start <= point.Second && point.Second <= end).ToArray();
            if (mark.HasValue)
            {
                double x = graph.X + graph.Width * (mark.Value - start) / (end - start);
                drawing.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, graph.Y), new Point(x, graph.Bottom));
            }
            Draw(drawing, graph, points, point => point.FrameMs, start, end, 33, Brushes.MediumPurple);
            Draw(drawing, graph, points, point => point.Cpu, start, end, 100, Brushes.PaleGreen);
            Draw(drawing, graph, points, point => point.Ping, start, end, 100, Brushes.SkyBlue);
            Write(drawing, "Время кадра", 30, 347, 15, Brushes.MediumPurple);
            Write(drawing, "CPU", 162, 347, 15, Brushes.PaleGreen);
            Write(drawing, "ICMP", 218, 347, 15, Brushes.SkyBlue);
            Write(drawing, $"{start:0.#}–{end:0.#} с · у линий разные шкалы", 315, 347, 15, Brushes.Gainsboro);
        }
        try
        {
            var bitmap = new RenderTargetBitmap(960, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var target = File.Create(dialog.FileName);
            encoder.Save(target);
            Status.Text = "PNG сохранён. Изображение содержит только метрики.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "Экспорт PNG · " + ex.Message; }
    }

    private static void Write(DrawingContext drawing, string text, double x, double y, double size, Brush color) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, 1), new Point(x, y));

    private static void Draw(DrawingContext drawing, Rect graph, MatchPoint[] samples, Func<MatchPoint, double?> value,
        double start, double end, double minimum, Brush color)
    {
        var scale = Math.Max(minimum, samples.Select(value).Max() ?? 0);
        var pen = new Pen(color, 2);
        Point? previous = null;
        foreach (var sample in samples)
        {
            if (value(sample) is not { } number) { previous = null; continue; }
            var current = new Point(graph.X + graph.Width * (sample.Second - start) / (end - start),
                graph.Bottom - graph.Height * number / scale);
            if (previous.HasValue) drawing.DrawLine(pen, previous.Value, current);
            previous = current;
        }
    }

    private void AddQuickSearch()
    {
        var quick = new RoutedCommand();
        InputBindings.Add(new KeyBinding(quick, Key.K, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(quick, (_, _) => OpenQuickSearch()));
    }

    private sealed record SearchAction(string Name, Action Run);

    private void OpenQuickSearch()
    {
        var commands = new List<SearchAction>
        {
            new("Сканирование Windows", () => Sections.SelectedItem = ScanTab),
            new("Защита Microsoft Defender", () => Sections.SelectedItem = DefenderTab),
            new("Игры Steam", () => Sections.SelectedItem = LibraryTab),
            new("Проверка сети", () => Sections.SelectedItem = ServersTab),
            new("Разбор фризов", () => OpenReviewClick(this, new RoutedEventArgs())),
            new("Рост папок", () => FolderGrowthClick(this, new RoutedEventArgs())),
            new("Проверить готовность к матчу", () =>
            {
                Sections.SelectedItem = LibraryTab;
                PreflightClick(this, new RoutedEventArgs());
            }),
            new("Восстановление автозапуска", () => Sections.SelectedItem = HistoryTab),
            new("Настройки", () => Sections.SelectedItem = SettingsTab)
        };
        commands.AddRange(matchHistory.Select(report => new SearchAction($"Сессия · {Path.GetFileNameWithoutExtension(report.Game)} · {report.At:g}",
            () => OpenHistoricalReport(report))));
        commands.AddRange(steamLibrary.Select(game => new SearchAction(game.Name, () =>
        {
            Sections.SelectedItem = LibraryTab;
            SteamGames.SelectedItem = game;
        })));
        commands.AddRange(items.Take(2500).Select(item => new SearchAction($"{item.Category} · {item.Name}", () =>
        {
            Sections.SelectedItem = ScanTab;
            Categories.SelectedIndex = 0;
            ReviewOnly.IsChecked = false;
            Search.Text = item.Name;
            Findings.SelectedItem = item;
            Findings.ScrollIntoView(item);
        })));
        ChooseItem("Быстрый поиск", commands, command => command.Name, searchable: true)?.Run();
    }

    private T? ChooseItem<T>(string title, IEnumerable<T> choices, Func<T, string> label, bool searchable = false) where T : class
    {
        var popup = new Window
        {
            Title = title, Owner = this, Width = 520, Height = 390, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        popup.SetResourceReference(BackgroundProperty, "Well");
        popup.SetResourceReference(ForegroundProperty, "Ink");
        var panel = new DockPanel { Margin = new Thickness(14) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox { DisplayMemberPath = "Label" };
        list.SetResourceReference(BackgroundProperty, "Panel");
        var choose = new Button { Content = "Открыть", IsDefault = true, Margin = new Thickness(0, 8, 0, 0) };
        var rows = choices.Select(item => new Choice<T>(item, label(item))).ToArray();
        if (searchable)
        {
            System.Windows.Automation.AutomationProperties.SetName(search, title);
            DockPanel.SetDock(search, Dock.Top);
            panel.Children.Add(search);
            search.TextChanged += (_, _) => Fill(search.Text.Trim());
        }
        DockPanel.SetDock(choose, Dock.Bottom);
        panel.Children.Add(choose);
        panel.Children.Add(list);
        popup.Content = panel;
        popup.Loaded += (_, _) => { if (searchable) search.Focus(); else list.Focus(); };
        popup.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) popup.Close();
            if (args.Key == Key.Down && search.IsKeyboardFocused) { list.Focus(); args.Handled = true; }
        };
        choose.Click += (_, _) => Finish();
        list.MouseDoubleClick += (_, _) => Finish();
        Fill("");
        return popup.ShowDialog() == true && list.SelectedItem is Choice<T> choice ? choice.Value : null;

        void Fill(string query)
        {
            list.ItemsSource = rows.Where(row => row.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(searchable ? 80 : 24).ToArray();
            list.SelectedIndex = 0;
        }
        void Finish()
        {
            if (list.SelectedItem is Choice<T>) popup.DialogResult = true;
        }
    }

    private sealed record Choice<T>(T Value, string Label);
}
