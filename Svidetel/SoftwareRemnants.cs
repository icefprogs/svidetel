namespace Svidetel;

public static class SoftwareRemnants
{
    public static List<Finding> Find(ScanResult? previous, ScanResult current, Func<string, bool> folderExists)
    {
        if (previous is not { HasSoftwareInventory: true, SoftwareObservedAt: { } observed } ||
            !current.HasSoftwareInventory)
            return [];

        var installed = current.Items.Where(item => item.Kind == "software").ToList();
        var locations = installed.Select(item => FullPath(item.InstallLocation)).OfType<string>().ToList();
        var results = new List<Finding>();
        foreach (var old in previous.Items.Where(item => item.IsMsi && item.Kind is "software" or "remnant"))
        {
            if (string.IsNullOrWhiteSpace(old.Name) || string.IsNullOrWhiteSpace(old.Publisher) ||
                string.IsNullOrWhiteSpace(old.State) || installed.Any(item => Same(item.Name, old.Name)))
                continue;

            var path = FullPath(old.InstallLocation);
            if (path is null || locations.Any(other => Within(other, path) || Within(path, other)) || !folderExists(path))
                continue;

            if (old.Kind == "remnant")
            {
                results.Add(old);
                continue;
            }

            var removal = current.Removals.FirstOrDefault(entry => entry.Time > observed &&
                entry.Time <= current.SoftwareObservedAt && Same(entry.Name, old.Name) &&
                Same(entry.Version, old.State) && Same(entry.Publisher, old.Publisher));
            if (removal is null) continue;

            results.Add(old with
            {
                Id = "remnant|" + old.Id, Kind = "remnant", Location = path, Command = "",
                Detail = $"Windows Installer подтвердил удаление {old.Name} {old.State} ({old.Publisher}). " +
                    $"Журнал «Приложение», MsiInstaller 1034, запись {removal.RecordId}, {removal.Time.ToLocalTime():g}.\n\n" +
                    "Папка из прежней записи установки всё ещё существует. " +
                    "Проверьте, нет ли в ней пользовательских данных или файлов других программ."
            });
        }
        return results.DistinctBy(item => item.Location, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsProgramFolder(string path)
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) return false;
            foreach (var root in roots.Where(root => root.Length > 0 && !Same(path, root) && Within(path, root)))
            {
                DirectoryInfo? dir = new(path);
                while (dir is not null && Within(dir.FullName, root))
                {
                    if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                    dir = dir.Parent;
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    private static string? FullPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
    }

    private static bool Same(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Within(string path, string root) =>
        Same(path, root) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
}
