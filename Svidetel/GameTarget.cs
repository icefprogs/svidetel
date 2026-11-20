using System.Net;
using System.Net.Sockets;

namespace Svidetel;

internal static class GameTarget
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Svidetel", "steam", "steamservice", "steamwebhelper", "gameoverlayui", "steamerrorreporter",
        "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment", "WinDivert", "winws", "winws2", "zapret",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "vgc", "vgtray",
        "csrss", "lsass", "services", "smss", "svchost", "winlogon", "MsMpEng", "SecurityHealthService",
        "powershell", "pwsh", "cmd", "wscript", "cscript", "rundll32", "mshta"
    };

    internal static bool CanSelect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':') ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        var name = Path.GetFileNameWithoutExtension(full);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        return !full.StartsWith(windows, StringComparison.OrdinalIgnoreCase) &&
               !Excluded.Contains(name) && !BackgroundApps.HasKnownName(name);
    }

    internal static bool TryProbe(string input, out IPAddress? address)
    {
        address = null;
        var value = input.Trim();
        if (value.Length == 0) return true;
        if (!IPAddress.TryParse(value, out var parsed)) return false;
        if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) || parsed.Equals(IPAddress.Broadcast) ||
            parsed.IsIPv6Multicast || parsed.AddressFamily == AddressFamily.InterNetwork && parsed.GetAddressBytes()[0] >= 224)
            return false;
        address = parsed;
        return true;
    }
}
