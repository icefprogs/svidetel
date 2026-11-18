using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Svidetel;
public sealed class Engine
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string DataDirectory { get; }
    public Engine(string? directory = null) { DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Svidetel"); Directory.CreateDirectory(DataDirectory); }
    public async Task<T> Call<T>(object request, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // A parent PowerShell 7 session can supply incompatible module paths to Windows PowerShell 5.1.
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\Modules") + ";" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsPowerShell\Modules");
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Bridge.ps1") }) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Windows PowerShell.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, Json)); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token); var text = await output; var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? text : stderr);
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new IOException("Пустой ответ сканера.");
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    public List<JournalEntry> History() => Read<List<JournalEntry>>("journal.json") ?? [];
    public T? Read<T>(string name) => File.Exists(Path.Combine(DataDirectory, name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(DataDirectory, name)), Json) : default;
    public void Save<T>(string name, T value) { var path = Path.Combine(DataDirectory, name); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), Encoding.UTF8); File.Move(temp, path, true); }
    public async Task Disable(Finding item)
    {
        using var actionLock = File.Open(Path.Combine(DataDirectory, "action.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!item.CanDisable) throw new InvalidOperationException("Объект доступен только для просмотра.");
        var before = await Call<JsonElement>(new { op = "snapshot", item });
        var entry = new JournalEntry { Item = item, Before = before }; var history = History(); history.Insert(0, entry); Save("journal.json", history);
        try { entry.After = await Call<JsonElement>(new { op = "disable", item, expected = before }); entry.Status = "disabled"; }
        catch (Exception ex) { entry.Status = "uncertain"; entry.Error = ex.Message; throw; }
        finally { Save("journal.json", history); }
    }
    public async Task Restore(JournalEntry entry)
    {
        using var actionLock = File.Open(Path.Combine(DataDirectory, "action.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Prepared/uncertain records stay recoverable after a crash: helper verifies the expected disabled state.
        var result = await Call<JsonElement>(new { op = "restore", item = entry.Item, expected = entry.Before });
        var history = History(); var stored = history.Single(x => x.Id == entry.Id); stored.After = result; stored.Status = "restored"; stored.Error = ""; Save("journal.json", history);
    }
}
