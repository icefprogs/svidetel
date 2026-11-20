using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Svidetel;

public partial class MainWindow
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Svidetel.Tray";
    private bool exitRequested;
    internal void AllowExit() => exitRequested = true;

    internal void StartInTray() => Tray().Visible = true;

    internal void OpenFromTray()
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private bool HideOnClose()
    {
        if (exitRequested || captureDemo || demo || CloseToTray.IsChecked != true) return false;
        StartInTray();
        Hide();
        return true;
    }

    private void ExitApplication()
    {
        if (changingProfile || busy) return;
        AllowExit();
        Close();
    }

    private static string? StartupCommand()
    {
        var path = Environment.ProcessPath;
        if (path is null || !Path.GetFileNameWithoutExtension(path).Equals("Svidetel", StringComparison.OrdinalIgnoreCase)) return null;
        return $"\"{path}\" --tray";
    }

    private void ReadStartup()
    {
        var command = StartupCommand();
        StartWithWindows.IsEnabled = command is not null && !captureDemo;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            StartWithWindows.IsChecked = command is not null && string.Equals(key?.GetValue(RunName) as string, command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartWithWindows.IsEnabled = false;
        }
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsReady || captureDemo || StartupCommand() is not { } command) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindows.IsChecked == true) key.SetValue(RunName, command);
            else if (string.Equals(key.GetValue(RunName) as string, command, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(RunName, false);
            Status.Text = StartWithWindows.IsChecked == true ? "При входе в Windows «Свидетель» откроется в трее" : "Автозапуск отключён";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            settingsReady = false;
            ReadStartup();
            settingsReady = true;
            Status.Text = "Автозапуск: " + ex.Message;
        }
    }
}
