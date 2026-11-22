using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record Preferences(int Theme = 0, int Accent = 0, bool Compact = false, bool Tray = false, bool Watch = false,
        bool CloseApps = false, bool HighPerformance = false, string GamePath = "", string ProbeAddress = "", bool BoostGame = false,
        bool LowerBackground = false, bool HideOnClose = true, bool NetworkMonitor = true, bool InternetProbe = true,
        bool NetworkAlerts = false, bool AutoSteam = true, int NetworkInterval = 0);
    private const string PreferenceFile = "preferences.json";
    private Preferences preferences = new();
    private bool settingsReady, lightTheme;
    private static readonly string[] BrushNames = ["Canvas", "Rail", "Panel", "Well", "Control", "SelectedRow", "Line", "Ink", "Muted", "OnAccent"];
    private static readonly string[] DarkPalette = ["#202124", "#1B1D20", "#26282C", "#1D2023", "#2C3035", "#2B3239", "#373B42", "#F1F3F5", "#B1B8C1", "#11202C"];
    private static readonly string[] LightPalette = ["#F3F5F8", "#E8EDF3", "#FFFFFF", "#F4F6F9", "#F8F9FB", "#DCEAF8", "#D2DAE4", "#1D2937", "#46576A", "#FFFFFF"];
    private static readonly string[] DarkAccents = ["#80CAF9", "#ACD6B2", "#F0C385"];
    private static readonly string[] LightAccents = ["#005E9C", "#2E653D", "#845000"];

    private void LoadSettings()
    {
        try { preferences = engine.Read<Preferences>(PreferenceFile) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Text = "Не удалось прочитать настройки оформления. Включены значения по умолчанию.";
        }
        ThemeChoice.SelectedIndex = Math.Clamp(preferences.Theme, 0, 2);
        AccentMode.SelectedIndex = Math.Clamp(preferences.Accent, 0, 2);
        CompactRows.IsChecked = preferences.Compact;
        MinimizeToTray.IsChecked = preferences.Tray;
        CloseToTray.IsChecked = preferences.HideOnClose;
        ReadStartup();
        WatchEnabled.IsChecked = preferences.Watch;
        AutoSteamChoice.IsChecked = preferences.AutoSteam;
        NetworkMonitorChoice.IsChecked = preferences.NetworkMonitor;
        InternetProbeChoice.IsChecked = preferences.InternetProbe;
        NetworkAlertsChoice.IsChecked = preferences.NetworkAlerts;
        NetworkIntervalChoice.SelectedIndex = Math.Clamp(preferences.NetworkInterval, 0, 2);
        CloseBackgroundApps.IsChecked = preferences.CloseApps;
        PreferPower.IsChecked = preferences.HighPerformance;
        LowerGameBackground.IsChecked = preferences.LowerBackground;
        ProbeIp.Text = preferences.ProbeAddress;
        BoostSelectedGame.IsChecked = preferences.BoostGame;
        UpdateGameSelection();
        settingsReady = true;
        ApplySettings();
        Activated += (_, _) =>
        {
            if (ThemeChoice.SelectedIndex == 2 && UsesLightTheme() != lightTheme) ApplySettings();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && preferences.Tray) TrayClick(this, new RoutedEventArgs());
        };
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        bool networkChanged = preferences.NetworkMonitor != (NetworkMonitorChoice.IsChecked == true)
            || preferences.InternetProbe != (InternetProbeChoice.IsChecked == true)
            || preferences.NetworkInterval != NetworkIntervalChoice.SelectedIndex;
        preferences = preferences with { Theme = ThemeChoice.SelectedIndex, Accent = AccentMode.SelectedIndex,
            Compact = CompactRows.IsChecked == true, Tray = MinimizeToTray.IsChecked == true, Watch = WatchEnabled.IsChecked == true,
            CloseApps = CloseBackgroundApps.IsChecked == true, HighPerformance = PreferPower.IsChecked == true,
            LowerBackground = LowerGameBackground.IsChecked == true, HideOnClose = CloseToTray.IsChecked == true,
            NetworkMonitor = NetworkMonitorChoice.IsChecked == true, InternetProbe = InternetProbeChoice.IsChecked == true,
            NetworkAlerts = NetworkAlertsChoice.IsChecked == true, AutoSteam = AutoSteamChoice.IsChecked == true,
            NetworkInterval = NetworkIntervalChoice.SelectedIndex };
        if (networkChanged) ResetAutomaticNetwork();
        ApplySettings();
        UpdateSessionControls();
        SavePreferences();
    }

    private void GameTuneChanged(object sender, RoutedEventArgs e)
    {
        if (settingsReady) UpdateSessionControls();
    }

    private void SavePreferences()
    {
        if (captureDemo) return;
        try { engine.Save(PreferenceFile, preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Настройки применены. Не удалось сохранить их на диске.";
        }
    }

    private static bool UsesLightTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private void ApplySettings()
    {
        lightTheme = ThemeChoice.SelectedIndex == 1 || ThemeChoice.SelectedIndex == 2 && UsesLightTheme();
        var palette = lightTheme ? LightPalette : DarkPalette;
        for (var i = 0; i < BrushNames.Length; i++) SetBrush(BrushNames[i], palette[i]);
        var accents = lightTheme ? LightAccents : DarkAccents;
        var accent = accents[Math.Clamp(AccentMode.SelectedIndex, 0, accents.Length - 1)];
        SetBrush("Accent", accent);
        SetBrush("Signal", accent);
        SetBrush("GpuSignal", lightTheme ? "#765225" : "#DBB984");
        SetBrush("CpuSignal", lightTheme ? "#52677E" : "#C0CBD7");
        SetBrush("NetworkSignal", lightTheme ? "#2B6B5C" : "#9FCDBB");
        SetBrush("EventSignal", lightTheme ? "#845000" : "#E7B87A");
        Findings.RowHeight = CompactRows.IsChecked == true ? 30 : 38;
        ApplyWindowTone(this);
    }

    private static void SetBrush(string name, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Application.Current.Resources[name] = brush;
    }

    private void ApplyWindowTone(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = lightTheme ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void ToolsClick(object sender, RoutedEventArgs e)
    {
        var menu = ((Button)sender).ContextMenu;
        menu.PlacementTarget = ToolsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LatestReleaseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/icefprogs/svidetel/releases/latest") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Status.Text = "Не удалось открыть GitHub. Проверьте браузер по умолчанию.";
        }
    }
}
