using System.Diagnostics;
using System.Windows;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record FolderSize(string Path, long Bytes);
    private sealed record FolderSnapshot(string Root, DateTimeOffset At, FolderSize[] Sizes);
    private const string GrowthFile = "folder-growth.json";

    private async void FolderGrowthClick(object sender, RoutedEventArgs e)
    {
        if (busy || demo || captureDemo || activeProfile == ProfileMode.Gamer) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку для замеров размера", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        var root = picker.FolderName;
        if (!Path.IsPathFullyQualified(root) || root.Length < 4 || !char.IsAsciiLetter(root[0]) || root[1] != ':')
        {
            Status.Text = "Выбери обычную папку на локальном диске.";
            return;
        }
        await Work(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            scanCancel = cancel;
            CancelButton.Visibility = Visibility.Visible;
            try
            {
                var sizes = await Task.Run(() => ReadFolderSizes(root, cancel.Token), cancel.Token);
                var previous = engine.Read<List<FolderSnapshot>>(GrowthFile) ?? [];
                var now = DateTimeOffset.Now;
                var comparable = previous.Where(item => SamePath(root, item.Root)).ToArray();
                var baseline = comparable.MinBy(item => (item.At - now.AddDays(-7)).Duration());
                var oldSizes = baseline?.Sizes.ToDictionary(item => item.Path, item => item.Bytes, StringComparer.OrdinalIgnoreCase);
                var updated = previous.Where(item => !SamePath(root, item.Root) || item.At.Date != now.Date).Prepend(new FolderSnapshot(root, now, sizes))
                    .Take(60).ToList();
                engine.Save(GrowthFile, updated);
                var rows = sizes.Select(item => new
                {
                    Folder = item,
                    Delta = oldSizes is null ? (long?)null : item.Bytes - oldSizes.GetValueOrDefault(item.Path)
                }).OrderByDescending(item => item.Delta ?? item.Folder.Bytes).Take(40).ToArray();
                Status.Text = baseline is null ? "Первый снимок папок сохранён." : $"Сравнение со снимком от {baseline.At:g}";
                var selected = ChooseItem(Status.Text, rows, item =>
                    $"{Path.GetFileName(item.Folder.Path)} · {item.Folder.Bytes / 1048576.0:N1} МБ" +
                    (item.Delta is { } delta ? $" · {delta / 1048576.0:+0.0;-0.0;0} МБ" : ""));
                if (selected is not null && Directory.Exists(selected.Folder.Path))
                    using (Process.Start(new ProcessStartInfo(selected.Folder.Path) { UseShellExecute = true })) { }
            }
            finally
            {
                CancelButton.Visibility = Visibility.Collapsed;
                scanCancel = null;
            }
        }, "Измеряю размеры вложенных папок…");
    }

    private static FolderSize[] ReadFolderSizes(string root, CancellationToken token)
    {
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Выбранная папка является ссылкой.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        };
        var rows = new List<FolderSize>();
        var scanned = 0;
        foreach (var child in directory.EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            long bytes = 0;
            foreach (var item in child.EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > 100_000) throw new IOException("Больше 100 000 объектов. Для сравнения выбери меньшую папку.");
                if (item is FileInfo file) bytes += file.Length;
            }
            rows.Add(new(child.FullName, bytes));
            if (rows.Count >= 1000) throw new IOException("Слишком много вложенных папок для одного снимка.");
        }
        return rows.ToArray();
    }
}
