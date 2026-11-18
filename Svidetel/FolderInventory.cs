namespace Svidetel;
public static class FolderInventory
{
    public static ScanResult Scan(string root, CancellationToken token, int limit = 50000)
    {
        var items = new List<Finding>(); var errors = new List<string>(); var queue = new Stack<string>(); queue.Push(Path.GetFullPath(root));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var dir = queue.Pop(); try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { errors.Add("Пропущена ссылка: " + dir); continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                {
                    token.ThrowIfCancellationRequested(); try
                    {
                        var attr = File.GetAttributes(path); if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attr & FileAttributes.Directory) != 0) { queue.Push(path); continue; }
                        var f = new FileInfo(path); items.Add(new() { Id = "file|" + path, Kind = "file", Name = f.Name, Command = path, Location = path, State = $"{f.Length:N0} bytes", Detail = $"Изменён UTC: {f.LastWriteTimeUtc:O}. Размер и расширение не определяют безопасность файла. Удаление не предлагается без доказательств." });
                        if (items.Count >= limit) { errors.Add($"Достигнут лимит {limit} файлов. Проверка неполная; выберите более узкую папку."); return new(items, errors); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (errors.Count < 500) errors.Add(path + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (errors.Count < 500) errors.Add(dir + ": " + ex.Message); }
        }
        errors.Add("Инвентаризация файлов завершена. Содержимое не анализировалось на вредоносность. Ссылки/reparse points пропущены."); return new(items, errors);
    }
}
