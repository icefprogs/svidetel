namespace Svidetel;

public static class BackgroundApps
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string[] ProgramFiles = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];

    private static readonly Dictionary<string, string[]> Locations = new(Names)
    {
        ["OneDrive"] = [.. InstallRoots(@"Microsoft OneDrive"), Path.Combine(Local, @"Microsoft\OneDrive")],
        ["Dropbox"] = [.. InstallRoots(@"Dropbox\Client"), Path.Combine(Local, @"Dropbox\Client")],
        ["GoogleDriveFS"] = InstallRoots(@"Google\Drive File Stream"),
        ["Spotify"] = [Path.Combine(Roaming, "Spotify"), .. InstallRoots("Spotify")],
        ["Creative Cloud"] = InstallRoots(@"Adobe\Adobe Creative Cloud")
    };

    private static string[] InstallRoots(string path) => ProgramFiles.Where(root => root.Length != 0)
        .Select(root => Path.Combine(root, path)).ToArray();

    public static bool HasKnownName(string name) => Locations.ContainsKey(name);

    public static bool IsRecognized(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Names.Equals(Path.GetExtension(path), ".exe") ||
            !Locations.TryGetValue(Path.GetFileNameWithoutExtension(path), out var roots))
            return false;
        var full = Path.GetFullPath(path);
        return roots.Any(root => Path.IsPathFullyQualified(root) && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
