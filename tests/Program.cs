using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Svidetel;
if (args.Contains("--fixture")) return;
if (args.Contains("--game-fixture"))
{
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    return;
}
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); checks++; }
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp",
    "Svidetel-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var e = new Engine(root);
    var frameCsv = new FrameCsv(41);
    frameCsv.Parse("Application,ProcessID,SwapChainAddress,CPUStartQPC,FrameTime,GPUBusy");
    Check(frameCsv.Parse("game.exe,41,0x11,123456,12.5,4.1") == new FrameLine("0x11", 12.5, 4.1, 123456) &&
        frameCsv.Parse("other.exe,42,0x11,123456,12.5,4.1") is null, "PresentMon v2 parser separates processes and preserves missing metrics");
    var match = new MatchRecorder(Path.Combine(root, "Fixture.exe"));
    Check(match.Finish().Frames == 0 && match.Finish().Fps is null, "session without frame capture has empty FPS metrics");
    Check(match.MarkFreeze() && !match.MarkFreeze(), "freeze hotkey coalesces repeated taps");
    for (int i = 0; i < 99; i++) match.AddFrame(new("main", 10, 2));
    match.AddFrame(new("main", 50, 10));
    match.AddFrame(new("overlay", 1000, 20));
    match.AddSample(67, 12);
    var recap = match.Finish();
    Check(recap.Frames == 100 && Math.Abs(recap.Fps!.Value - 1000 / 10.4) < .001 && recap.Low1 == 20 && recap.P99 == 10,
        "session metrics use one swap chain and separately compute 1% low and P99");
    Check(recap.Points is [{ FrameMs: 50, Cpu: 67, Ping: 12 }] && recap.Marks.Length == 1,
        "freeze timeline aligns local CPU and ping with the dominant frame chain");
    var legacy = JsonSerializer.Deserialize<MatchResult>(
        """{"Game":"Fixture.exe","At":"2026-11-21T19:12:00+05:00","Frames":100,"Fps":96,"Low1":20,"P99":10,"Points":[{"Second":2,"FrameMs":50,"Cpu":67,"Ping":12}],"Marks":[2],"Limited":false}""", Engine.Json);
    Check(legacy is { Points: [{ Ping: 12, RouterProbed: false }], Marks.Length: 1, AutomaticMarks: null },
        "3.3 session history opens without fabricated fields");
    var detector = new FreezeDetector();
    for (int index = 0; index < 120; index++) detector.Observe(10, index / 100.0);
    detector.Observe(49, 2);
    detector.Observe(68, 3);
    detector.Observe(69, 3.1);
    detector.Observe(71, 5.5);
    Check(detector.Marks.SequenceEqual(new double[] { 3, 5.5 }) && detector.Live() is { LastMs: 71 },
        "automatic freezes require a baseline and do not duplicate nearby spikes");
    var menuFrame = new MatchRecorder("Fixture");
    for (int index = 0; index < 140; index++) menuFrame.AddFrame(new("game", 16, null));
    for (int index = 0; index < 120; index++) menuFrame.AddFrame(new("menu", 20, null));
    menuFrame.AddFrame(new("menu", 300, null));
    Check(menuFrame.Finish() is { AutomaticMarks.Length: 0, LongFrames: 0 }, "secondary swap chains cannot mark a game freeze");
    const string steamManifest = "\"AppState\"\r\n{\r\n\t\"appid\"\t\t\"123\"\r\n\t\"name\"\t\t\"Пример\"\r\n\t\"installdir\"\t\t\"Example\"\r\n\t\"StateFlags\"\t\t\"260\"\r\n}";
    var steamGame = SteamLibrary.Parse(steamManifest, root);
    Check(steamGame is { Id: 123, Name: "Пример", Updating: true } && steamGame.Folder == Path.Combine(root, "common", "Example"),
        "Steam parses installed manifests with Unicode and an update flag");
    Check(SteamLibrary.Parse(steamManifest.Replace("Example", @"..\\Example"), root) is null,
        "Steam manifest install name cannot leave the library");
    var steamClock = new DateTime(2026, 11, 22, 13, 0, 0);
    Check(SteamLibrary.LatestDownloadRate("[2026-11-22 12:59:59] Current download rate: 18.250 Mbps", steamClock) == 18.25 &&
        SteamLibrary.LatestDownloadRate("[2026-11-22 12:00:00] Current download rate: 18.250 Mbps", steamClock) is null,
        "Steam traffic display excludes stale download rate entries");
    foreach (var app in new[] { "steam", "Discord", "WinDivert", "winws", "EasyAntiCheat_EOS" })
        Check(!GameTarget.CanSelect(Path.Combine(root, app + ".exe")), $"{app} cannot be selected for a priority boost");
    Check(GameTarget.CanSelect(Path.Combine(root, "DemoGame.exe")) && !GameTarget.CanSelect("DemoGame.exe"), "only an absolute local game executable can be selected");
    Check(GameTarget.TryProbe("127.0.0.1", out var loopback) && loopback!.Equals(IPAddress.Loopback) &&
        GameTarget.TryProbe(" ", out var none) && none is null &&
        !GameTarget.TryProbe("https://example.com", out _) && !GameTarget.TryProbe("ff02::1", out _) &&
        !GameTarget.TryProbe("::ffff:224.0.0.1", out _), "latency configuration accepts one unicast IP or the gateway default");
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    Check(BackgroundApps.IsRecognized(Path.Combine(local, @"Microsoft\OneDrive\OneDrive.exe")), "known OneDrive install root can be optimized");
    Check(!BackgroundApps.IsRecognized(Path.Combine(local, @"Microsoft\OneDriveBeta\OneDrive.exe")) &&
        !BackgroundApps.IsRecognized(Path.Combine(local, @"Microsoft\OneDrive\..\..\OneDrive.exe")), "lookalike roots and paths outside the install are excluded");
    foreach (var process in new[] { "steam", "Discord", "WinDivert", "winws", "Game" })
        Check(!BackgroundApps.HasKnownName(process), $"{process} is excluded from automatic changes");
    var telemetry = new GameTelemetry.Snapshot("fixture", true, [new(4, 31), new(8, 25), new(null, 92), new(30, 80), new(34, 20)]);
    Check(telemetry.Median == 19 && telemetry.Jitter == 4 && telemetry.Missing == 1, "latency metrics exclude missing replies and do not bridge gaps");
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
    if (args.Contains("--game-integration"))
    {
        var exe = Path.Combine(root, "WitnessGameFixture.exe");
        File.Copy(Environment.ProcessPath!, exe);
        var assembly = typeof(GameTarget).Assembly.GetName().Name;
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, assembly + extension), Path.Combine(root, assembly + extension));
        var start = new ProcessStartInfo(exe, "--game-fixture")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true
        };
        var profile = new PerformanceProfile(e);
        var progress = new Progress<PerformanceProfile.Progress>();
        profile.Begin(false, false, false, progress);
        try
        {
            Check(profile.ObserveGame(exe, true).State == PerformanceProfile.GameState.Waiting, "game profile waits until the selected game starts");
            using var game = Process.Start(start)!;
            try
            {
                if (await game.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) != "READY")
                    throw new Exception("Game fixture did not start");
                game.PriorityClass = ProcessPriorityClass.Normal;
                var read = profile.ObserveGame(exe, true);
                game.Refresh();
                Check(read.State == PerformanceProfile.GameState.Boosted && read.Pid == game.Id && read.Memory > 0 &&
                      game.PriorityClass == ProcessPriorityClass.AboveNormal, "late-start game PID is measured and raised to AboveNormal");
                profile.End();
                game.Refresh();
                Check(game.PriorityClass == ProcessPriorityClass.Normal, "ending the session restores the game priority");
                profile.Begin(false, false, false, progress);
                profile.ObserveGame(exe, true);
                game.PriorityClass = ProcessPriorityClass.BelowNormal;
                profile.End();
                game.Refresh();
                Check(game.PriorityClass == ProcessPriorityClass.BelowNormal, "restoration preserves a priority set externally after the boost");
            }
            finally
            {
                await game.StandardInput.WriteLineAsync("QUIT");
                await game.WaitForExitAsync();
            }
        }
        finally { profile.End(); }
        var ping = new GameTelemetry(IPAddress.Loopback);
        using var pingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var measured = await ping.Measure(pingTimeout.Token);
        Check(measured.History.Length == 1 && measured.History[0].Latency.HasValue, "explicit IP path replies over real loopback");
        var loopbackServer = await ServerProbe.Compare([("fixture", IPAddress.Loopback)], pingTimeout.Token);
        Check(loopbackServer is [{ Replies: 4, Median: not null }], "server scoring uses four independent loopback replies");
    }
    Console.WriteLine($"{checks} checks passed");
}
finally { Directory.Delete(root, true); }
