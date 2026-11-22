using System.Net;
using System.Windows;

namespace Svidetel;

public partial class MainWindow
{
    private const string NetworkRunsFile = "network-runs.json";
    private sealed record NetworkMeasurement(string Adapter, string Router, string Target, long?[] RouterMs, long?[] TargetMs,
        long? ReceiveKbps, long? SendKbps);
    private sealed record NetworkRun(string Label, DateTimeOffset At, NetworkMeasurement Idle, NetworkMeasurement Loaded);
    private bool measuringNetwork;
    private NetworkMeasurement? idleNetwork, loadedNetwork;
    private NetworkMeasurement? steamNetworkBefore;
    private bool steamNetworkCompared;
    private int steamSeenSamples;
    private List<NetworkRun> networkRuns = [];

    private void LoadNetworkRuns()
    {
        networkRuns = engine.Read<List<NetworkRun>>(NetworkRunsFile) ?? [];
        ShowNetworkRuns();
    }

    private async void IdleNetworkClick(object sender, RoutedEventArgs e) => await TestNetwork(underLoad: false);
    private async void LoadedNetworkClick(object sender, RoutedEventArgs e) => await TestNetwork(underLoad: true);

    private async Task TestNetwork(bool underLoad)
    {
        if (measuringNetwork || demo || captureDemo || activeProfile == ProfileMode.Gamer) return;
        if (underLoad && idleNetwork is null)
        {
            NetworkLoadResult.Text = "Сначала измерь задержку в простое.";
            return;
        }
        if (!GameTarget.TryProbe(ProbeIp.Text, out var target))
        {
            NetworkLoadResult.Text = "Укажи один IPv4 или IPv6 в настройках сессии.";
            return;
        }
        target ??= preferences.InternetProbe ? NetworkRoutes.Find()?.Reference : null;
        var route = NetworkRoutes.Find(target);
        if (route is null && target is null)
        {
            NetworkLoadResult.Text = "Windows не нашла активное подключение.";
            return;
        }
        var adapter = route?.Adapter.Id ?? "";
        var routerAddress = route?.Router?.ToString() ?? "";
        var targetAddress = target?.ToString() ?? "";
        if (underLoad && idleNetwork is { } baseline &&
            (baseline.Adapter != adapter || baseline.Router != routerAddress || baseline.Target != targetAddress))
        {
            NetworkLoadResult.Text = "Сеть или адрес изменились. Повтори замер в простое.";
            return;
        }
        measuringNetwork = true;
        NetworkLoadButton.IsEnabled = false;
        NetworkLoadResult.Text = "Восемь параллельных замеров…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var measurement = await MeasureConnection(target, 8, timeout.Token);
            if (measurement is null || measurement.Adapter != adapter || measurement.Router != routerAddress)
            {
                NetworkLoadResult.Text = "Сеть изменилась во время проверки. Повтори замер.";
                return;
            }
            if (underLoad) loadedNetwork = measurement;
            else
            {
                idleNetwork = measurement;
                loadedNetwork = null;
            }
            ShowNetworkRuns();
        }
        catch (OperationCanceledException) { NetworkLoadResult.Text = "Время проверки истекло."; }
        finally
        {
            measuringNetwork = false;
            NetworkLoadButton.IsEnabled = idleNetwork is not null;
        }
    }

    private static async Task<NetworkMeasurement?> MeasureConnection(IPAddress? target, int count, CancellationToken token)
    {
        var before = NetworkRoutes.Find(target);
        if (before is null && target is null) return null;
        var telemetry = new GameTelemetry(target);
        var readings = new List<GameTelemetry.Sample>(count);
        for (int i = 0; i < count; i++)
        {
            var shot = await telemetry.Measure(token);
            readings.Add(shot.History[^1]);
            if (i < count - 1) await Task.Delay(320, token);
        }
        var after = NetworkRoutes.Find(target);
        if (before?.Key != after?.Key) return null;
        return new(before?.Adapter.Id ?? "", before?.Router?.ToString() ?? "", target?.ToString() ?? "",
            before?.Router is null ? [] : readings.Select(s => s.Router).ToArray(),
            target is null ? [] : readings.Select(s => s.Latency).ToArray(),
            readings.Max(s => s.ReceiveKbps), readings.Max(s => s.SendKbps));
    }

    private void ShowSteamNetworkComparison(GameTelemetry.Snapshot snapshot)
    {
        if (steamNetworkCompared || steamNetworkBefore is not { } before || ++steamSeenSamples < 4 || snapshot.History.Length < 4) return;
        var current = snapshot.Connection;
        if ((current?.AdapterId ?? "") != before.Adapter || (current?.Router?.ToString() ?? "") != before.Router ||
            (current?.Target?.ToString() ?? "") != before.Target)
        {
            steamNetworkCompared = true;
            match?.AddEvent("Сеть после запуска Steam сменилась, сравнение задержки пропущено");
            return;
        }
        var points = snapshot.History.TakeLast(4).ToArray();
        var after = new NetworkMeasurement(before.Adapter, before.Router, before.Target,
            before.Router.Length == 0 ? [] : points.Select(point => point.Router).ToArray(),
            before.Target.Length == 0 ? [] : points.Select(point => point.Latency).ToArray(),
            points.Max(point => point.ReceiveKbps), points.Max(point => point.SendKbps));
        var result = $"Steam · до запуска {DescribeNetwork(before)}\nПосле запуска · {DescribeNetwork(after)}";
        SteamStatus.Text = result + "\nICMP до роутера и выбранного узла. Задержку матча показывает игра.";
        match?.AddEvent(result.Replace('\n', ' '));
        steamNetworkCompared = true;
    }

    private void SaveNetworkRunClick(object sender, RoutedEventArgs e)
    {
        if (demo || captureDemo || idleNetwork is null || loadedNetwork is null) return;
        var label = NetworkLabel.Text.Trim();
        if (label.Length == 0) { NetworkLabel.Focus(); return; }
        SaveNetworkRun(label, idleNetwork, loadedNetwork);
        ShowNetworkRuns();
    }

    private void SaveNetworkRun(string label, NetworkMeasurement idle, NetworkMeasurement loaded)
    {
        networkRuns.Insert(0, new(label, DateTimeOffset.Now, idle, loaded));
        if (networkRuns.Count > 12) networkRuns.RemoveRange(12, networkRuns.Count - 12);
        TrySave(NetworkRunsFile, networkRuns);
        ShowSavedNetworkRuns();
    }

    private void ShowNetworkRuns()
    {
        NetworkLoadButton.IsEnabled = !measuringNetwork && idleNetwork is not null;
        SaveNetworkPanel.Visibility = loadedNetwork is not null ? Visibility.Visible : Visibility.Collapsed;
        NetworkResultsTable.Visibility = idleNetwork is null ? Visibility.Collapsed : Visibility.Visible;
        NetworkRouterIdle.Text = DescribeReplies(idleNetwork?.RouterMs ?? []);
        NetworkTargetIdle.Text = DescribeReplies(idleNetwork?.TargetMs ?? []);
        NetworkRouterLoaded.Text = DescribeReplies(loadedNetwork?.RouterMs ?? []);
        NetworkTargetLoaded.Text = DescribeReplies(loadedNetwork?.TargetMs ?? []);
        NetworkTargetLabel.Text = idleNetwork is { Target.Length: > 0 } target ? target.Target : "Выбранный IP";
        NetworkLoadResult.Text = loadedNetwork is { } loaded && idleNetwork is { } idle
            ? $"Пиковый приём · {Format(idle.ReceiveKbps / 1000.0)} → {Format(loaded.ReceiveKbps / 1000.0)} Мбит/с"
            : idleNetwork is not null ? "Замер в простое завершён." : "";
        ShowSavedNetworkRuns();
    }

    private void ShowSavedNetworkRuns()
    {
        SavedNetworkRuns.Visibility = networkRuns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedNetworkText.Text = string.Join("\n\n", networkRuns.Select(run =>
            $"{run.Label} · {run.At:g}\nПростой · {DescribeNetwork(run.Idle)}\nПри загрузке · {DescribeNetwork(run.Loaded)}"));
    }

    private static string DescribeNetwork(NetworkMeasurement measurement) =>
        $"роутер {DescribeReplies(measurement.RouterMs)} · узел {DescribeReplies(measurement.TargetMs)} · пик приёма {(measurement.ReceiveKbps.HasValue ? $"{measurement.ReceiveKbps} кбит/с" : "—")}";

    private static string DescribeReplies(long?[] samples)
    {
        if (samples.Length == 0) return "—";
        var replies = samples.OfType<long>().Order().ToArray();
        return replies.Length == 0 ? $"без ответа ({samples.Length}/{samples.Length})"
            : $"{PassiveNetworkWindow.Median(replies):0.#} мс · пропуски {samples.Length - replies.Length}/{samples.Length}";
    }
}
