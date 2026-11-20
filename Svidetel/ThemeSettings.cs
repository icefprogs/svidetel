using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record Preferences(int Theme = 0, int Accent = 0, bool Compact = false, bool Tray = false, bool Watch = false,
        bool CloseApps = false, bool HighPerformance = false, string GamePath = "", string ProbeAddress = "", bool BoostGame = false,
        bool LowerBackground = true, bool HideOnClose = true);
    private const string PreferenceFile = "preferences.json";
    private Preferences preferences = new();
    private bool settingsReady, lightTheme;
    private static readonly string[] BrushNames = ["Canvas", "Panel", "Well", "Control", "SelectedRow", "Line", "Ink", "Muted", "OnAccent"];
    private static readonly string[] DarkPalette = ["#191A21", "#23252F", "#1D1F29", "#323543", "#414063", "#424655", "#F0F1F8", "#B9BDC9", "#202232"];
    private static readonly string[] LightPalette = ["#EDEFEA", "#FBFCF8", "#F2F5EF", "#E3E8DF", "#D9E4D7", "#CFD8CC", "#1E251E", "#4F5D50", "#FBFCF8"];
    private static readonly string[] DarkAccents = ["#C5DBBE", "#C6D7FF", "#F6D196"];
    private static readonly string[] LightAccents = ["#36563D", "#355890", "#73511F"];

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
        preferences = preferences with { Theme = ThemeChoice.SelectedIndex, Accent = AccentMode.SelectedIndex,
            Compact = CompactRows.IsChecked == true, Tray = MinimizeToTray.IsChecked == true, Watch = WatchEnabled.IsChecked == true,
            CloseApps = CloseBackgroundApps.IsChecked == true, HighPerformance = PreferPower.IsChecked == true,
            LowerBackground = LowerGameBackground.IsChecked == true, HideOnClose = CloseToTray.IsChecked == true };
        ApplySettings();
        SavePreferences();
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
        SetBrush("Accent", accents[Math.Clamp(AccentMode.SelectedIndex, 0, accents.Length - 1)]);
        SetBrush("Signal", lightTheme ? "#3F6453" : "#AACCB7");
        SetBrush("GlassControl", lightTheme ? "#DCE5E0" : "#4A554F");
        SetGlassBrush("SessionHero", "#635EAA", "#494878", 12);
        SetGlassBrush("GlassFace", lightTheme ? "#EFFFFFFF" : "#383F4843", lightTheme ? "#9DF9FBF9" : "#161D2421", 90);
        SetGlassBrush("GlassRim", lightTheme ? "#FFFDFEFF" : "#8FAAB9AF", lightTheme ? "#A7C8D1CA" : "#354A5650", 110);
        SetGlassBrush("GlassReflection", lightTheme ? "#F5FFFFFF" : "#46C8D5CD", "#00FFFFFF", 110);
        Findings.RowHeight = CompactRows.IsChecked == true ? 30 : 38;
        ApplyWindowTone(this);
    }

    private static void SetBrush(string name, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Application.Current.Resources[name] = brush;
    }

    private static void SetGlassBrush(string name, string highlight, string shadow, double angle)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(highlight),
            (Color)ColorConverter.ConvertFromString(shadow), angle);
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
}
