using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Svidetel;

public partial class MainWindow
{
    private Forms.NotifyIcon? tray;
    private IReadOnlyList<ProcessOrigin> processOrigins = [];
    private bool preparingOptimization;

    private void LoadProcessOrigins()
    {
        try { processOrigins = engine.Read<List<ProcessOrigin>>("process-origins.json") ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Text = "Сохранённый журнал запуска не удалось прочитать.";
        }
    }
    private readonly bool captureDemo = Environment.GetCommandLineArgs().Contains("--capture-demo");

    private static void Capture(FrameworkElement content, string name)
    {
        content.UpdateLayout();
        const int inset = 24;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth) + inset * 2,
            (int)Math.Ceiling(content.ActualHeight) + inset * 2, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(27, 29, 28)), null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
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
        LoadDemo();
        Loaded += (_, _) =>
        {
            Findings.SelectedIndex = 0;
            Capture((FrameworkElement)Content, "overview.png");
            OptimizeClick(this, new RoutedEventArgs());
            Close();
        };
    }

    private string Evidence(Finding item)
    {
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
        if (string.IsNullOrWhiteSpace(item.Executable)) return note;
        var origin = item.ProcessStartedAt is { } started
            ? processOrigins.FirstOrDefault(entry => entry.ProcessId == item.ProcessId &&
                Math.Abs((entry.Time - started).TotalSeconds) < 3 &&
                string.Equals(entry.Executable, item.Executable, StringComparison.OrdinalIgnoreCase))
            : null;
        if (origin != null)
            note += $"\n\nЖурнал безопасности Windows · событие 4688, запись {origin.RecordId}\nЗапуск: {origin.Time.ToLocalTime():g}\nСоздавшее приложение: {origin.ParentExecutable}";
        var matches = items.Where(other => other.Id != item.Id &&
            (item.Kind == "process" ? other.Kind is "run" or "startup" or "service" or "task" : other.Kind == "process") &&
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
        void Open() => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("Открыть Свидетель", null, (_, _) => Open());
        menu.Items.Add("Выйти", null, (_, _) => Dispatcher.Invoke(() => { if (!busy) Close(); }));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Свидетель", ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        tray.BalloonTipClicked += (_, _) => Open();
        Closed += (_, _) => { tray.Dispose(); menu.Dispose(); };
        return tray;
    }

    private void NotifyReturn(string message)
    {
        var icon = Tray();
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
            processOrigins = await engine.Call<List<ProcessOrigin>>(new { op = "process-origin" });
            engine.Save("process-origins.json", processOrigins);
            if (Findings.SelectedItem is Finding item) ShowFinding(item);
            Status.Text = processOrigins.Count == 0
                ? "Событий 4688 за последние сутки нет. Проверьте, включён ли аудит создания процессов."
                : $"Сохранено событий запуска: {processOrigins.Count}. Совпадения показаны в карточках процессов.";
        }, "Читаю журнал безопасности Windows…");
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
            Background = new SolidColorBrush(Color.FromRgb(27, 29, 28)), Content = panel
        };
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var apply = new Button { Content = "Применить выбранное", IsEnabled = false, Background = new SolidColorBrush(Color.FromRgb(54, 94, 72)) };
        actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = "Отмена", IsCancel = true });
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
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (captureDemo)
            dialog.Loaded += (_, _) =>
            {
                Capture((FrameworkElement)dialog.Content, "optimization.png");
                dialog.Close();
            };
        if (dialog.ShowDialog() != true || demo || selected.Count == 0) return;
        await Work(async () =>
        {
            var failures = new List<string>();
            var disabled = 0;
            var closeRequests = 0;
            foreach (var item in selected)
            {
                try
                {
                    if (item.Kind == "process") { InteractiveProcess.RequestClose(item); closeRequests++; }
                    else { await engine.Disable(item); disabled++; }
                }
                catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
            }
            Status.Text = $"Отключено: {disabled}. Запросов на закрытие: {closeRequests}. Не выполнено: {failures.Count}.";
            if (failures.Count > 0)
                MessageBox.Show(this, string.Join("\n", failures), "Часть записей не изменена", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Отключаю выбранные точки запуска…");
    }
}
