using System.Text.Json;
namespace Svidetel;
public sealed record Finding
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    public string State { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool CanDisable { get; init; }
    public string Executable { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedAt { get; init; }
    public bool MissingTarget { get; init; }
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public bool IsMsi { get; init; }
    public string Category => Kind switch { "run" or "policy" => "Реестр", "task" => "Задачи", "service" => "Службы", "driver" => "Драйверы", "startup" => "Папки запуска", "process" => "Процессы", "wmi" => "WMI", "software" or "package" => "Программы", "network" => "Сеть", "extension" => "Расширения", "remnant" => "Остатки", _ => Kind };
    public string Recommendation => Kind == "remnant" ? "Проверить оставшуюся папку" : MissingTarget ? "Проверить путь запуска" : CanDisable ? "Проверить необходимость запуска" : "Только просмотр";
    public string SearchText => $"{Name} {Kind} {Category} {Command} {Location} {Detail}";
}
public sealed record ScanResult(List<Finding> Items, List<string> Errors)
{
    public DateTimeOffset? SoftwareObservedAt { get; init; }
    public List<MsiRemoval> Removals { get; init; } = [];
    public LaunchEvidence Launches { get; init; } = new([], [], []);
    public bool HasSoftwareInventory => SoftwareObservedAt.HasValue &&
        !Errors.Any(error => error.StartsWith("Installed software:", StringComparison.Ordinal));
}
public sealed record InventoryObservation(DateTimeOffset ObservedAt, string Changes);
public sealed record ProcessOrigin(long RecordId, DateTimeOffset Time, int ProcessId, string Executable, string ParentExecutable)
{
    public string Source { get; init; } = "Security 4688";
}
public sealed record TaskLaunch(long RecordId, DateTimeOffset Time, int ProcessId, string TaskPath);
public sealed record LaunchEvidence(List<ProcessOrigin> Processes, List<TaskLaunch> Tasks, List<string> Errors);
public sealed record MsiRemoval(long RecordId, DateTimeOffset Time, string Name, string Version, string Publisher);
public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public Finding Item { get; set; } = new();
    public JsonElement Before { get; set; }
    public JsonElement? After { get; set; }
    public string Status { get; set; } = "prepared";
    public string Error { get; set; } = "";
    public string Label => $"{Time:dd.MM HH:mm} · {Item.Name} · {Status}";
}
public static class InventoryDiff
{
    public static string Describe(IEnumerable<Finding> previous, IEnumerable<Finding> current)
    {
        var old = previous.Where(x => x.Kind is not ("process" or "network")).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var now = current.Where(x => x.Kind is not ("process" or "network")).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var added = now.Keys.Except(old.Keys).Select(k => "+ " + now[k].Name);
        var removed = old.Keys.Except(now.Keys).Select(k => "− " + old[k].Name);
        var changed = now.Keys.Intersect(old.Keys).Where(k => now[k].Command != old[k].Command || now[k].State != old[k].State).Select(k => "~ " + now[k].Name);
        return string.Join(Environment.NewLine, added.Concat(removed).Concat(changed));
    }
}
