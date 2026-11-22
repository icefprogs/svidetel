using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Svidetel;

internal sealed record SteamGame(uint Id, string Name, string Folder, bool Updating)
{
    public override string ToString() => Name;
}

internal static partial class SteamLibrary
{
    internal static SteamGame[] Read()
    {
        var root = FindRoot();
        if (root is null) return [];
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var index = ReadSmall(Path.Combine(root, "steamapps", "libraryfolders.vdf"), 2_000_000);
        if (index is not null)
        {
            foreach (Match entry in KeyValue().Matches(index))
            {
                if (entry.Groups[1].Value != "path") continue;
                var path = Unescape(entry.Groups[2].Value);
                if (IsLocal(path)) libraries.Add(path);
                if (libraries.Count >= 32) break;
            }
        }

        Dictionary<uint, SteamGame> games = [];
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(1500))
                {
                    if (ReadSmall(manifest, 256_000) is not { } text) continue;
                    var game = Parse(text, apps);
                    if (game is not null && Directory.Exists(game.Folder)) games.TryAdd(game.Id, game);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static SteamGame? Parse(string text, string apps)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in KeyValue().Matches(text))
            fields.TryAdd(match.Groups[1].Value, Unescape(match.Groups[2].Value));
        if (!fields.TryGetValue("appid", out var raw) || !uint.TryParse(raw, out var id) || id == 0 ||
            !fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !fields.TryGetValue("installdir", out var folder) || folder is "" or "." or ".." ||
            folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.EndsWith(' ') || folder.EndsWith('.'))
            return null;
        var updating = uint.TryParse(fields.GetValueOrDefault("StateFlags"), out uint state) && (state & 256) != 0;
        return new(id, name, Path.Combine(apps, "common", folder), updating);
    }

    private static string? FindRoot()
    {
        var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return IsLocal(root) && File.Exists(Path.Combine(root, "steam.exe")) ? root : null;
    }

    internal static double? DownloadRate()
    {
        try
        {
            if (FindRoot() is not { } root) return null;
            using var stream = new FileStream(Path.Combine(root, "logs", "content_log.txt"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 32_768), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return LatestDownloadRate(reader.ReadToEnd(), DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal static double? LatestDownloadRate(string tail, DateTime now)
    {
        var rates = DownloadReading().Matches(tail);
        if (rates.Count == 0) return null;
        var latest = rates[^1];
        if (!DateTime.TryParseExact(latest.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ||
            now - timestamp > TimeSpan.FromSeconds(40) || timestamp - now > TimeSpan.FromSeconds(2)) return null;
        return double.TryParse(latest.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) ? rate : null;
    }

    private static string? ReadSmall(string path, long maximum)
    {
        try { return new FileInfo(path).Length <= maximum ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");
    private static bool IsLocal(string path) => Path.IsPathFullyQualified(path) &&
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && !path.AsSpan(2).Contains(':');

    [GeneratedRegex("""^[ \t]*"([^"\r\n]+)"[ \t]+"((?:\\[^\r\n]|[^"\\\r\n])*)"[ \t]*\r?$""",
        RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex KeyValue();

    [GeneratedRegex("""^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\]\s+Current download rate: ([0-9.]+) Mbps""",
        RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex DownloadReading();
}
