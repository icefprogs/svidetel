using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record Preferences(int Theme = 0, int Accent = 0, bool Compact = false, bool Tray = false, bool Watch = false);
    private const string PreferenceFile = "preferences.json";
    private Preferences preferences = new();
    private bool settingsReady, lightTheme;
    private static readonly string[] BrushNames = ["Canvas", "Panel", "Well", "Control", "SelectedRow", "Line", "Ink", "Muted", "OnAccent"];
    private static readonly string[] DarkPalette = ["#191A19", "#222422", "#1C1E1C", "#30332F", "#353D36", "#414641", "#F0F2ED", "#B4B9B1", "#19291B"];
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
        WatchEnabled.IsChecked = preferences.Watch;
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
        preferences = new(ThemeChoice.SelectedIndex, AccentMode.SelectedIndex, CompactRows.IsChecked == true,
            MinimizeToTray.IsChecked == true, WatchEnabled.IsChecked == true);
        ApplySettings();
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
}
