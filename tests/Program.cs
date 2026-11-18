using System.Text.Json;
using Svidetel;
if (args.Contains("--fixture")) return;
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); checks++; }
var root = Path.Combine(Path.GetTempPath(), "Svidetel-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var e = new Engine(root);
    var f = new Finding { Id = "1", Kind = "task", Name = "Updater", State = "Ready", Command = "old" };
    var diff = InventoryDiff.Describe([f, new() { Id = "gone", Name = "Gone" }], [f with { Command = "new" }, new() { Id = "new", Name = "New" }]);
    Check(diff.Contains("~ Updater") && diff.Contains("− Gone") && diff.Contains("+ New"), "inventory detects changes/additions/removals");
    Check(InventoryDiff.Describe([new() { Id = "p", Kind = "process" }], []) == "", "volatile processes excluded from diff");
    e.Save("journal.json", new List<JournalEntry> { new() { Item = f, Before = JsonSerializer.SerializeToElement(new { Exists = true, Value = "a\"; $(bad)\nтест" }) } });
    Check(e.History()[0].Before.GetProperty("Value").GetString()!.Contains("тест"), "journal preserves exact Unicode/quoted data");
    e.Save("journal.json", new List<JournalEntry>()); Check(e.History().Count == 0, "atomic journal replacement");
    Directory.CreateDirectory(Path.Combine(root, "folder")); File.WriteAllText(Path.Combine(root, "folder", "тест.txt"), "abc");
    var scan = FolderInventory.Scan(Path.Combine(root, "folder"), CancellationToken.None); Check(scan.Items.Count == 1 && !scan.Items[0].CanDisable, "file inventory cannot disable/delete files");
    using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { FolderInventory.Scan(root, cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "folder inventory cancellation"); }
    try { await e.Disable(new() { CanDisable = false }); throw new Exception("Read-only accepted"); } catch (InvalidOperationException) { Check(true, "engine blocks read-only changes before starting helper"); }
    try { await e.Call<JsonElement>(new { op = "snapshot", item = new Finding { Kind = "run", Name = "X", Location = @"HKCU:\Software\Unrelated" } }); throw new Exception("Key accepted"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("Unsupported registry key"), "helper rejects arbitrary registry paths"); }
    try { await e.Call<JsonElement>(new { op = "snapshot", item = new Finding { Kind = "task", Name = "X", Location = @"\Microsoft\Windows\" } }); throw new Exception("Task accepted"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("Protected"), "helper protects Microsoft tasks"); }
    try { await e.Call<JsonElement>(new { op = "snapshot", item = new Finding { Kind = "startup", Name = "X", Location = Path.Combine(root, "x.lnk") } }); throw new Exception("Path accepted"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("Not a direct"), "helper rejects startup path outside fixed roots"); }
    try { await e.Call<JsonElement>(new { op = "snapshot", item = new Finding { Kind = "process" } }); throw new Exception("Process accepted"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("Read-only"), "helper rejects unsupported mutation sources"); }
    if (args.Contains("--scan"))
    {
        var result = await e.Call<ScanResult>(new { op = "scan" }); Check(result.Items.Count > 0, "live read-only Windows scan returns inventory");
        Check(result.Items.Select(x => x.Id).Distinct().Count() == result.Items.Count, "live finding IDs are unique");
        Console.WriteLine($"Inventory: {result.Items.Count} entries, {result.Errors.Count} coverage errors"); foreach (var error in result.Errors) Console.WriteLine(error);
    }
    if (args.Contains("--integration"))
    {
        const string subkey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var name = "Svidetel_Test_" + Guid.NewGuid().ToString("N");
        var command = "\"" + Environment.ProcessPath + "\" --fixture";
        using var registry = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subkey);
        registry.SetValue(name, command, Microsoft.Win32.RegistryValueKind.ExpandString);
        var item = new Finding { Kind = "run", Id = "test", Name = name, Location = @"HKCU:\" + subkey, Command = command, CanDisable = true };
        try
        {
            await e.Disable(item); Check(registry.GetValue(name) == null, "real registry fixture disabled");
            var saved = e.History().Single(); Check(saved.Status == "disabled", "successful mutation journaled");
            var quiet = await e.Call<JsonElement>(new { op = "monitor", entries = new[] { saved } }); Check(quiet.GetProperty("Returned").GetArrayLength() == 0, "monitor sees disabled entry as absent");
            await e.Restore(saved); Check((string?)registry.GetValue(name) == command && registry.GetValueKind(name) == Microsoft.Win32.RegistryValueKind.ExpandString, "restore preserves exact value and type");
            await e.Disable(item); saved = e.History().First(); registry.SetValue(name, "changed", Microsoft.Win32.RegistryValueKind.String);
            var returned = await e.Call<JsonElement>(new { op = "monitor", entries = new[] { saved } }); Check(returned.GetProperty("Returned").GetArrayLength() == 1, "monitor detects recreated entry");
            try { await e.Restore(saved); throw new Exception("Conflict overwritten"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("recreated"), "restore refuses overwrite after external recreation"); }
            registry.DeleteValue(name); await e.Restore(saved); Check((string?)registry.GetValue(name) == command, "recovery works after conflict removed");
        }
        finally { registry.DeleteValue(name, false); }
    }
    Console.WriteLine($"{checks} checks passed");
}
finally { Directory.Delete(root, true); }
