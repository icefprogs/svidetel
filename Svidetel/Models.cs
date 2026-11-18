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
    public string Category => Kind switch { "run" => "Реестр", "task" => "Задачи", "service" => "Службы", "driver" => "Драйверы", "startup" => "Папки запуска", "process" => "Процессы", "wmi" => "WMI", "software" => "Программы", "network" => "Сеть", "extension" => "Расширения", _ => Kind };
    public string Recommendation => CanDisable ? "Проверить необходимость запуска" : "Только просмотр / защищено";
    public string SearchText => $"{Name} {Kind} {Category} {Command} {Location} {Detail}";
}
public sealed record ScanResult(List<Finding> Items, List<string> Errors);
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
        var old = previous.Where(x => x.Kind is not ("process" or "network")).ToDictionary(x => x.Id);
        var now = current.Where(x => x.Kind is not ("process" or "network")).ToDictionary(x => x.Id);
        var added = now.Keys.Except(old.Keys).Select(k => "+ " + now[k].Name);
        var removed = old.Keys.Except(now.Keys).Select(k => "− " + old[k].Name);
        var changed = now.Keys.Intersect(old.Keys).Where(k => now[k].Command != old[k].Command || now[k].State != old[k].State).Select(k => "~ " + now[k].Name);
        return string.Join(Environment.NewLine, added.Concat(removed).Concat(changed));
    }
}
