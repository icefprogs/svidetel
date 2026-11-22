using System.Windows;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace Svidetel;

public partial class MainWindow
{
    private readonly PassiveNetworkWindow connectionWindow = new();
    private GameTelemetry? networkSampler;
    private bool networkClosed;
    private int networkChange, detectedNetworkChange = -1, initialNetworkSamples;
    private DateTimeOffset nextNetworkSample, lastLiveNetworkSample, lastAutoNetworkSave, lastNetworkNotice;
    private string lastAutomaticSaveKey = "", lastNetworkIssue = "";

    private void StartAutomaticNetwork()
    {
        if (captureDemo || demo) return;
        ResetAutomaticNetwork();
        var stopping = new CancellationTokenSource();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChange;
        Closed += (_, _) =>
        {
            networkClosed = true;
            stopping.Cancel();
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChange;
        };
        _ = WatchAutomaticNetwork(stopping);
    }

    private async Task WatchAutomaticNetwork(CancellationTokenSource stopping)
    {
        using (stopping)
        {
            var token = stopping.Token;
            using var clock = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                do
                {
                    await Dispatcher.InvokeAsync(() => RunAutomaticNetwork(token), DispatcherPriority.Background, token)
                        .Task.Unwrap().ConfigureAwait(false);
                }
                while (await clock.WaitForNextTickAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { }
        }
    }

    private void OnNetworkAddressChange(object? sender, EventArgs args) => Interlocked.Increment(ref networkChange);

    private void ResetAutomaticNetwork()
    {
        Interlocked.Increment(ref networkChange);
        nextNetworkSample = DateTimeOffset.MinValue;
        if (!preferences.NetworkMonitor) AutomaticNetworkStatus.Text = "Автоматическая проверка выключена";
    }

    private async Task RunAutomaticNetwork(CancellationToken stopping)
    {
        if (networkClosed || demo || !preferences.NetworkMonitor || activeProfile == ProfileMode.Gamer)
            return;
        int epoch = Volatile.Read(ref networkChange);
        if (epoch != detectedNetworkChange)
        {
            detectedNetworkChange = epoch;
            networkSampler = new(automaticInternet: preferences.InternetProbe);
            connectionWindow.Reset();
            initialNetworkSamples = 3;
            nextNetworkSample = DateTimeOffset.MinValue;
            AutomaticNetworkStatus.Text = "Проверяю текущее подключение…";
        }
        if (DateTimeOffset.Now < nextNetworkSample) return;
        var sampler = networkSampler!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var snapshot = await Task.Run(() => sampler.Measure(timeout.Token, refreshRoute: true), timeout.Token);
            if (!networkClosed && epoch == Volatile.Read(ref networkChange) && preferences.NetworkMonitor && activeProfile != ProfileMode.Gamer)
                ReadAutomaticNetwork(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (NetworkInformationException)
        {
            if (!networkClosed) AutomaticNetworkStatus.Text = "Windows не предоставила сведения о подключении";
        }
        finally
        {
            nextNetworkSample = DateTimeOffset.Now.AddSeconds(initialNetworkSamples-- > 0 ? 1 : preferences.NetworkInterval switch
            {
                0 => 8,
                2 => 30,
                _ => 15
            });
        }
    }

    private void ReadGameNetwork(GameTelemetry.Snapshot snapshot)
    {
        if (!preferences.NetworkMonitor || DateTimeOffset.Now - lastLiveNetworkSample < TimeSpan.FromSeconds(4)) return;
        int epoch = Volatile.Read(ref networkChange);
        if (epoch != detectedNetworkChange)
        {
            detectedNetworkChange = epoch;
            connectionWindow.Reset();
            networkSampler = new(automaticInternet: preferences.InternetProbe);
            nextNetworkSample = DateTimeOffset.MinValue;
        }
        lastLiveNetworkSample = DateTimeOffset.Now;
        ReadAutomaticNetwork(snapshot);
    }

    private void ReadAutomaticNetwork(GameTelemetry.Snapshot snapshot)
    {
        var connection = snapshot.Connection;
        if (connection is null || snapshot.History.LastOrDefault() is not { } reading)
        {
            connectionWindow.Reset();
            AutomaticNetworkStatus.Text = "Windows не нашла активный маршрут";
            AutomaticNetworkDetail.Text = "Проверка возобновится при подключении.";
            NetworkAdapterInfo.Text = "—";
            AutomaticRouter.Text = AutomaticInternet.Text = AutomaticTraffic.Text = "—";
            AutomaticDnsInfo.Text = "";
            NetworkIpInfo.Text = "";
            AutomaticRouterAddress.Text = AutomaticInternetAddress.Text = "—";
            AutoNetworkComparison.Text = "";
            return;
        }
        var now = DateTimeOffset.Now;
        connectionWindow.Add(connection.Key, now, reading);
        var recent = connectionWindow.Recent();
        var router = PassiveNetworkWindow.Results(recent, router: true);
        var external = PassiveNetworkWindow.Results(recent, router: false);
        var observation = PassiveNetworkWindow.Evaluate(router, external);
        NetworkAdapterInfo.Text = $"{ConnectionType(connection.Type)} · {connection.Adapter}" +
            (connection.LinkSpeed > 0 ? $" · линк {connection.LinkSpeed / 1_000_000.0:0.#} Мбит/с" : "");
        AutomaticRouterAddress.Text = connection.Router is { } gateway
            ? $"{gateway}" + (connection.RouterAdapter is { } local ? $" · {local}" : "")
            : "Адрес шлюза недоступен";
        AutomaticInternetAddress.Text = connection.Target is { } target
            ? (connection.Automatic ? $"Cloudflare · {target}" : target.ToString()) : "Внешний ICMP выключен";
        AutomaticRouter.Text = DescribeReplies(router);
        AutomaticInternet.Text = DescribeReplies(external);
        var down = reading.ReceiveKbps / 1000.0;
        var up = reading.SendKbps / 1000.0;
        AutomaticTraffic.Text = $"↓ {Format(down)}  ↑ {Format(up)} Мбит/с";
        AutomaticNetworkStatus.Text = observation switch
        {
            NetworkObservation.Warming => "Собираю ответы",
            NetworkObservation.Responding => "Контрольные адреса отвечают",
            NetworkObservation.RouterSilent => "Роутер пропускает ICMP, внешний узел отвечает",
            NetworkObservation.WanSilent => "Внешний узел пропускает ICMP, роутер отвечает",
            NetworkObservation.BothSilent => "Контрольные адреса пропускают ICMP",
            _ => "Есть пропуски контрольных запросов"
        };
        AutomaticNetworkDetail.Text = (observation is NetworkObservation.RouterSilent or NetworkObservation.WanSilent or NetworkObservation.BothSilent
            ? "ICMP может фильтроваться · " : "") + $"Обновлено в {now:HH:mm:ss}";
        NetworkIpInfo.Text = connection.Addresses.Length == 0 ? "Адреса не получены" : string.Join(" · ", connection.Addresses);
        AutomaticDnsInfo.Text = connection.Dns.Length == 0 ? "DNS-адреса не получены" : "DNS Windows · " + string.Join(" · ", connection.Dns);
        var (idle, load) = connectionWindow.Cohorts();
        AutoNetworkComparison.Text = CompareTraffic(connection, idle, load);
        CheckNetworkNotice(observation, now);
    }

    private string CompareTraffic(NetworkConnection connection, GameTelemetry.Sample[] idle, GameTelemetry.Sample[] load)
    {
        if (idle.Length < 5 || load.Length < 5)
            return $"Простой {idle.Length}/5 · Трафик {load.Length}/5. Сравнение появится при обычном использовании сети.";
        var first = AsMeasurement(connection, idle);
        var second = AsMeasurement(connection, load);
        var firstSamples = (connection.Target is null ? first.RouterMs : first.TargetMs).OfType<long>().Order().ToArray();
        var secondSamples = (connection.Target is null ? second.RouterMs : second.TargetMs).OfType<long>().Order().ToArray();
        string details = $"Простой · {DescribeNetwork(first)}\nТрафик · {DescribeNetwork(second)}";
        if (firstSamples.Length < 3 || secondSamples.Length < 3)
            return details + "\nОтветов недостаточно, чтобы сравнить задержку.";
        var delta = PassiveNetworkWindow.Median(secondSamples) - PassiveNetworkWindow.Median(firstSamples);
        var now = DateTimeOffset.Now;
        if (lastAutomaticSaveKey != connection.Key || now - lastAutoNetworkSave >= TimeSpan.FromHours(1))
        {
            lastAutomaticSaveKey = connection.Key;
            lastAutoNetworkSave = now;
            SaveNetworkRun($"Авто · {ConnectionType(connection.Type)}", first, second);
        }
        return details + $"\nРазница медиан · {delta:+0.#;-0.#;0} мс" +
            (delta >= 25 ? "\nПри трафике задержка выросла. Проверьте фоновые загрузки, если это мешает игре." : "");
    }

    private static NetworkMeasurement AsMeasurement(NetworkConnection connection, GameTelemetry.Sample[] samples) =>
        new(connection.AdapterId, connection.Router?.ToString() ?? "", connection.Target?.ToString() ?? "",
            PassiveNetworkWindow.Results(samples, router: true), PassiveNetworkWindow.Results(samples, router: false),
            samples.Max(point => point.ReceiveKbps), samples.Max(point => point.SendKbps));

    private void CheckNetworkNotice(NetworkObservation observation, DateTimeOffset now)
    {
        var issue = observation is NetworkObservation.BothSilent or NetworkObservation.WanSilent
            ? AutomaticNetworkStatus.Text : "";
        if (issue.Length == 0) { lastNetworkIssue = ""; return; }
        if (issue == lastNetworkIssue || !preferences.NetworkAlerts || now - lastNetworkNotice < TimeSpan.FromMinutes(10)) return;
        lastNetworkIssue = issue;
        lastNetworkNotice = now;
        if (QueueGameAlert(issue)) return;
        var trayIcon = Tray();
        trayIcon.Visible = true;
        openLastReportFromNotification = false;
        trayIcon.ShowBalloonTip(5000, "Свидетель · сеть", issue, System.Windows.Forms.ToolTipIcon.Info);
    }

    private static string ConnectionType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "Туннель",
        _ => "Сетевой интерфейс"
    };

    private void NetworkWindowsClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:network-status");
    private void NetworkAdaptersClick(object sender, RoutedEventArgs e) => OpenSettingsPage(
        Environment.OSVersion.Version.Build >= 22000 ? "ms-settings:network-advancedsettings" : "ms-settings:network-status");
    private void NetworkProxyClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:network-proxy");
    private void NetworkVpnClick(object sender, RoutedEventArgs e) => OpenSettingsPage("ms-settings:network-vpn");
}
