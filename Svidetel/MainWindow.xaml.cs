using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace Svidetel;
public partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); ApplyWindowTone(this); }
    readonly Engine engine = new(); List<Finding> items = []; bool busy, demo; CancellationTokenSource? scanCancel;
    static readonly string[] categories = ["Все", "Реестр", "Задачи", "Службы", "Драйверы", "Папки запуска", "Процессы", "WMI", "Программы", "Остатки", "Сеть", "Расширения", "Файлы"];
    readonly System.Windows.Threading.DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    bool watching; string lastAlert = "";
    public MainWindow()
    {
        InitializeComponent();
        Categories.ItemsSource = categories;
        Categories.SelectedIndex = 0;
        AdminLabel.Text = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
            ? "Администратор" : "Обычные права";
        RefreshHistory();
        LoadSettings();
        LoadProcessOrigins();
        watchTimer.Tick += async (_, _) => await Watch();
        watchTimer.Start();
        Closed += (_, _) => watchTimer.Stop();
        if (Environment.GetCommandLineArgs().Contains("--demo")) LoadDemo();
        PrepareDemoCapture();
    }
    async Task Watch() { if (busy || watching || demo || WatchEnabled.IsChecked != true) return; watching = true; try { var entries = engine.History().GroupBy(x => x.Item.Id).Select(x => x.First()).Where(x => x.Status == "disabled").ToArray(); if (entries.Length == 0) return; var result = await engine.Call<JsonElement>(new { op = "monitor", entries }); var returned = result.GetProperty("Returned").EnumerateArray().Select(x => x.GetString()).ToArray(); var text = string.Join("\n", returned); if (text.Length > 0 && text != lastAlert) { lastAlert = text; Status.Text = "Внимание: отключённые записи вернулись. " + text; NotifyReturn(text); } if (text.Length == 0) lastAlert = ""; var errors = result.GetProperty("Errors").GetArrayLength(); if (errors > 0) Status.Text = $"Наблюдение неполное: ошибок доступа {errors}."; } catch (Exception ex) { Status.Text = "Наблюдение: " + ex.Message; } finally { watching = false; } }
    void SetBusy(bool value, string message) { busy = value; Toolbar.IsEnabled = !value; OptimizeButton.IsEnabled = !value; RestoreButton.IsEnabled = !value; Status.Text = message; UpdateActions(); }
    void UpdateActions()
    {
        var item = Findings.SelectedItem as Finding;
        var canDisable = !demo && item?.CanDisable == true;
        var canInspect = !demo && item is not null &&
            (item.Executable.Length > 0 || item.Kind == "file" && item.Command.Length > 0 || item.Kind == "remnant");
        DisableButton.Visibility = canDisable ? Visibility.Visible : Visibility.Collapsed;
        InspectButton.Visibility = canInspect ? Visibility.Visible : Visibility.Collapsed;
        DisableButton.IsEnabled = InspectButton.IsEnabled = !busy;
        InspectButton.Content = item?.Kind == "remnant" ? "Открыть папку" : "Подпись и SHA-256";
    }
    async Task Work(Func<Task> operation, string message) { if (busy) return; SetBusy(true, message); try { await operation(); } catch (OperationCanceledException) { Status.Text = "Проверка отменена."; } catch (Exception ex) { Status.Text = "Операция не завершена. См. сообщение и историю."; MessageBox.Show(this, ex.Message, "Свидетель", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { SetBusy(false, Status.Text); RefreshHistory(); } }
    async void ScanClick(object sender, RoutedEventArgs e) => await ScanSystem();
    async Task<bool> ScanSystem()
    {
        var completed = false;
        await Work(async () =>
        {
        scanCancel = new(); CancelButton.Visibility = Visibility.Visible;
        try
        {
            var result = await engine.Call<ScanResult>(new { op = "scan" }, scanCancel.Token);
            var previous = engine.Read<ScanResult>("inventory.json");
            var softwareBaseline = engine.Read<ScanResult>("software-inventory.json") ?? previous;
            result.Items.AddRange(SoftwareRemnants.Find(softwareBaseline, result, SoftwareRemnants.IsProgramFolder));
            if (result.HasSoftwareInventory)
                engine.Save("software-inventory.json", new ScanResult(result.Items.Where(x => x.Kind is "software" or "remnant").ToList(), [])
                    { SoftwareObservedAt = result.SoftwareObservedAt });
            SaveLaunches(result.Launches);
            var diff = previous == null ? "Первый снимок. Сравнение появится после следующего сканирования." : InventoryDiff.Describe(previous.Items, result.Items);
            var observations = engine.Read<List<InventoryObservation>>("observations.json") ?? [];
            if (previous is { Errors.Count: 0 } && result.Errors.Count == 0 && !string.IsNullOrWhiteSpace(diff))
            {
                observations.Insert(0, new InventoryObservation(DateTimeOffset.Now, diff));
                engine.Save("observations.json", observations.Take(20).ToList());
            }
            var accessErrors = result.Errors.Concat(result.Launches.Errors).ToList();
            Coverage.Text =
                "Источники снимка\nRun/RunOnce, политики входа, Winlogon Shell/Userinit, задачи, службы, драйверы, папки запуска, процессы, WMI, программы и AppX/MSIX текущего пользователя, TCP, UDP, расширения Chrome, Edge, Brave, Vivaldi, Яндекс Браузера и Firefox.\n\n" +
                $"Журналы запуска за последние 24 часа\nПроцессы: {launchEvidence.Processes.Count}. Задачи: {launchEvidence.Tasks.Count}. Используются включённые и доступные журналы. События показаны в карточках объектов.\n\n" +
                "Остатки программ\nДля подтверждения нужно два успешных снимка списка установленного ПО, совпадающее событие удаления MSI и сохранившаяся папка установки.\n\n" +
                "Ошибки и ограничения\n" + (accessErrors.Count == 0 ? "Ошибок источников не получено." : string.Join("\n", accessErrors)) +
                "\n\nИзменения относительно предыдущего снимка\n" + (string.IsNullOrWhiteSpace(diff) ? "Изменений в снимке нет." : diff) +
                (accessErrors.Count > 0 ? "\nПри ошибке источника исчезнувшая из снимка запись требует перепроверки." : "");
            if (observations.Count > 0) Coverage.Text += "\n\nИстория наблюдений (время сканирования)\n" + string.Join("\n\n", observations.Select(o => $"{o.ObservedAt:g}\n{o.Changes}"));
            engine.Save("inventory.json", result with { Launches = new([], [], []), Removals = [] });
            items = result.Items; demo = false; ApplyFilter();
            Status.Text = $"Объектов: {items.Count} · пробелы в источниках: {accessErrors.Count}";
            completed = true;
        }
        finally { CancelButton.Visibility = Visibility.Collapsed; scanCancel.Dispose(); scanCancel = null; }
        }, "Собираю сведения… Это может занять несколько минут.");
        return completed;
    }
    void ApplyFilter()
    {
        if (Findings == null || Search == null || Categories == null) return;
        var cat = Categories.SelectedItem as string ?? "Все";
        var review = ReviewOnly?.IsChecked == true;
        var selected = (Findings.SelectedItem as Finding)?.Id;
        var visible = items.Where(x => (cat == "Все" || x.Category == cat || cat == "Файлы" && x.Kind == "file") &&
            (!review || x.MissingTarget || x.Kind == "remnant") &&
            x.SearchText.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        Findings.ItemsSource = visible;
        if (selected is not null) Findings.SelectedItem = visible.Find(item => item.Id == selected);
        UpdateActions();
    }
    void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    void FilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    void ReviewFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();
    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Findings.SelectedItem is Finding item) ShowFinding(item);
        else Details.Text = "Выберите объект, чтобы открыть сведения.";
        UpdateActions();
    }
    private void ShowFinding(Finding f) => Details.Text = $"{f.Name}\nИсточник: {f.Category}\nСостояние: {f.State}\nРасположение: {f.Location}\nКоманда: {f.Command}\n\n{f.Detail}\n\n{f.Recommendation}{Evidence(f)}\nДоступность кнопки не означает рекомендацию отключить объект. Службы и задачи могут обеспечивать обновления, резервное копирование и защиту.";
    async void DisableClick(object sender, RoutedEventArgs e) { if (demo || Findings.SelectedItem is not Finding f) return; if (MessageBox.Show(this, $"Отключить автоматический запуск «{f.Name}»?\n\nПрограмма может перестать обновляться или выполнять фоновые функции. Работающий процесс/служба не будет остановлен. Исходные настройки сохранятся для восстановления. Системные компоненты и непроверяемые команды будут заблокированы.", "Подтверждение изменения", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; await Work(async () => { await engine.Disable(f); Status.Text = "Запуск отключён и результат проверен. Обновите сканирование. Восстановление доступно в истории."; }, "Сохраняю параметры и отключаю запуск…"); }
    async void RestoreClick(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is not JournalEntry entry || entry.Status == "restored") return; if (MessageBox.Show(this, $"Вернуть исходные настройки «{entry.Item.Name}»?", "Восстановление", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await Work(async () => { await engine.Restore(entry); Status.Text = "Исходные параметры восстановлены. Обновите сканирование."; }, "Восстанавливаю…"); }
    async void InspectClick(object sender, RoutedEventArgs e)
    {
        if (Findings.SelectedItem is not Finding item || demo) return;
        if (item.Kind == "remnant")
        {
            if (!SoftwareRemnants.IsProgramFolder(item.Location)) { Status.Text = "Папка больше недоступна. Обновите снимок."; return; }
            try
            {
                var explorer = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                explorer.ArgumentList.Add(item.Location);
                using var process = Process.Start(explorer);
            }
            catch (System.ComponentModel.Win32Exception ex) { Status.Text = ex.Message; }
            return;
        }
        await Work(async () =>
        {
            var path = item.Executable.Length == 0 ? item.Command : "\"" + item.Executable + "\"";
            var data = await engine.Call<JsonElement>(new { op = "inspect", command = path });
            Details.Text += "\n\n" + JsonSerializer.Serialize(data, Engine.Json);
            Status.Text = "Подпись и SHA-256 проверены локально.";
        }, "Проверяю файл…");
    }
    void RefreshHistory() { try { HistoryList.ItemsSource = engine.History(); } catch (Exception ex) { Status.Text = "Ошибка чтения журнала: " + ex.Message; } }
    void HistoryClick(object sender, RoutedEventArgs e) => RefreshHistory();
    void CopyClick(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Details.Text); } catch (Exception ex) { Status.Text = ex.Message; } }
    void CancelClick(object sender, RoutedEventArgs e) => scanCancel?.Cancel();
    void DeveloperLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; }
    void AppsClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
    void ExportClick(object sender, RoutedEventArgs e) { if (MessageBox.Show(this, "Отчёт содержит имена пользователей, пути, команды и сетевые адреса. Не публикуйте его без проверки. Сохранить локально?", "Экспорт", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "svidetel-report.json" }; if (dlg.ShowDialog() == true) { try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(new { Created = DateTimeOffset.Now, Demo = demo, Items = items, Coverage = Coverage.Text }, Engine.Json)); Status.Text = "Отчёт сохранён локально."; } catch (Exception ex) { MessageBox.Show(ex.Message); } } }
    void AdminClick(object sender, RoutedEventArgs e) { try { var path = Environment.ProcessPath ?? throw new IOException("Путь приложения недоступен."); if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("Для повышения прав запустите Svidetel.exe из папки сборки."); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); Close(); } catch (Exception ex) { Status.Text = "Повышение прав не выполнено: " + ex.Message; } }
    void DemoClick(object sender, RoutedEventArgs e) => LoadDemo();
    void LoadDemo()
    {
        demo = true;
        items =
        [
            new() { Id = "demo1", Kind = "task", Name = "Cloud Studio Updater", Command = @"C:\Demo\Studio\Updater.exe --silent",
                Location = @"\Studio\", State = "Готово", Detail = "Демонстрационная задача обновления. Запускается каждый час.", CanDisable = true },
            new() { Id = "demo2", Kind = "service", Name = "Archive Desk Backup", Command = @"C:\Demo\Archive\Backup.exe",
                State = "Running / Auto", Detail = "Демонстрационная служба резервного копирования.", CanDisable = true },
            new() { Id = "demo3", Kind = "extension", Name = "Reading Mode", State = "Активно в профиле",
                Location = @"Firefox / default", Detail = "Демонстрационное расширение Firefox." },
            new() { Id = "demo4", Kind = "process", Name = "Field Notes", Executable = @"C:\Demo\Notes.exe", State = "Открыто",
                Detail = "Демонстрационный процесс с открытым окном." },
            new() { Id = "demo5", Kind = "remnant", Name = "Pixel Archive", State = "2.4",
                Location = @"C:\Program Files\Pixel Archive",
                Detail = "Пример: Windows Installer подтвердил удаление. Папка из прежней записи установки всё ещё существует." }
        ];
        ApplyFilter();
        Coverage.Text = "Демонстрационные данные.";
        Status.Text = "Деморежим · вымышленные объекты";
    }
    async void FolderClick(object sender, RoutedEventArgs e) { var dlg = new OpenFolderDialog { Title = "Выберите папку для инвентаризации (без удаления)" }; if (dlg.ShowDialog() != true) return; await Work(async () => { scanCancel = new(); CancelButton.Visibility = Visibility.Visible; try { var result = await Task.Run(() => FolderInventory.Scan(dlg.FolderName, scanCancel.Token)); items = result.Items; demo = false; Coverage.Text = string.Join("\n", result.Errors); ApplyFilter(); Status.Text = $"Файлов: {items.Count}. Только просмотр. {result.Errors.Count} сообщений об охвате."; } finally { CancelButton.Visibility = Visibility.Collapsed; scanCancel.Dispose(); scanCancel = null; } }, "Читаю файлы выбранной папки…"); }
}
