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
    var install = new Finding
    {
        Id = "app", Kind = "software", Name = "Test App", State = "1.2", Publisher = "Test Co",
        IsMsi = true, InstallLocation = @"C:\Program Files\Test App"
    };
    var now = DateTimeOffset.UtcNow;
    var baseline = new ScanResult([install], []) { SoftwareObservedAt = now.AddDays(-1) };
    var removal = new MsiRemoval(91, now.AddHours(-1), install.Name, install.State, install.Publisher);
    var current = new ScanResult([], []) { SoftwareObservedAt = now, Removals = [removal] };
    bool Exists(string path) => path == install.InstallLocation;
    var remnants = SoftwareRemnants.Find(baseline, current, Exists);
    Check(remnants.Count == 1 && !remnants[0].CanDisable && remnants[0].Detail.Contains("91"), "matching MSI success and existing former install path yields read-only evidence");
    Check(SoftwareRemnants.Find(baseline, current with { Removals = [] }, Exists).Count == 0, "missing registration alone is insufficient");
    Check(SoftwareRemnants.Find(baseline, current with { Removals = [removal with { Publisher = "Different Co" }] }, Exists).Count == 0, "same-name uninstall from another publisher is excluded");
    Check(SoftwareRemnants.Find(baseline, current with { Removals = [removal with { Time = now.AddDays(-2) }] }, Exists).Count == 0, "uninstall before the observed install is excluded");
    Check(SoftwareRemnants.Find(baseline, current with { Items = [install with { State = "1.3" }] }, Exists).Count == 0, "same-name upgrade is excluded");
    Check(SoftwareRemnants.Find(baseline, current with { Items = [install with { Name = "Other", InstallLocation = install.InstallLocation + @"\Tools" }] }, Exists).Count == 0, "another installation using the folder is excluded");
    Check(SoftwareRemnants.Find(baseline, current with { Errors = ["Installed software: denied"] }, Exists).Count == 0, "incomplete software inventory is insufficient");
    Check(SoftwareRemnants.Find(baseline, current, _ => false).Count == 0, "absent folder is excluded");
    Check(SoftwareRemnants.Find(current with { Items = remnants }, current with { Removals = [] }, Exists).Count == 1, "previously corroborated evidence survives later scans");
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
        Check(result.HasSoftwareInventory && result.Items.Any(x => x.Kind == "software"), "live Windows uninstall metadata is available");
        var launches = await e.Call<LaunchEvidence>(new { op = "launch-evidence" });
        Check(launches.Processes.All(x => x.ProcessId > 0 && x.Executable.Length > 0) &&
            launches.Tasks.All(x => x.ProcessId > 0 && x.TaskPath.StartsWith('\\')), "live provenance command returns valid event records");
        Console.WriteLine($"Inventory: {result.Items.Count} entries, {result.Errors.Count} coverage errors"); foreach (var error in result.Errors) Console.WriteLine(error);
        foreach (var error in result.Launches.Errors) Console.WriteLine(error);
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
