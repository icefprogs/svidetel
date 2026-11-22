using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Svidetel;

public partial class MainWindow
{
    private const int OverlayHotkey = 0x522;
    private bool overlayHotkeyRegistered;
    private int sampleGamePid;
    private Window? gameOverlay;
    private TextBlock? overlayContent;
    private bool gameSettingsLoaded;
    private readonly HashSet<string> quietAlerts = new(StringComparer.Ordinal);
    private bool openLastReportFromNotification;

    private void OverlayChanged(object sender, RoutedEventArgs e)
    {
        if (LiveOverlayChoice?.IsChecked != true) CloseOverlay();
        else UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (match is null || demo || captureDemo || LiveOverlayChoice.IsChecked != true) return;
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint pid);
        if (sampleGamePid <= 0 || pid != (uint)sampleGamePid)
        {
            if (gameOverlay?.IsVisible == true) gameOverlay.Hide();
            return;
        }
        if (gameOverlay is null)
        {
            overlayContent = new TextBlock { Foreground = Brushes.White, FontFamily = new("Segoe UI"), FontSize = 14 };
            gameOverlay = new Window
            {
                AllowsTransparency = true, Background = Brushes.Transparent, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, Focusable = false, IsHitTestVisible = false,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(232, 23, 24, 32)),
                    Padding = new Thickness(13, 7, 13, 7), CornerRadius = new CornerRadius(9), Child = overlayContent
                }
            };
            gameOverlay.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(gameOverlay).Handle;
                const int extendedStyle = -20;
                const int transparentNoActivate = 0x08000020;
                _ = SetWindowLong(hwnd, extendedStyle, GetWindowLong(hwnd, extendedStyle) | transparentNoActivate);
            };
        }
        if (match.Live() is { } live)
            overlayContent!.Text = $"{live.Fps:0} FPS   ·   1% low {Format(live.Low1)}   ·   {live.LastMs:0.#} мс";
        else overlayContent!.Text = "Ожидаю кадры PresentMon";

        var area = System.Windows.Forms.Screen.FromHandle(foreground).WorkingArea;
        if (!gameOverlay.IsVisible) gameOverlay.Show();
        var source = PresentationSource.FromVisual(gameOverlay);
        var origin = source?.CompositionTarget?.TransformFromDevice.Transform(new Point(area.Left + 16, area.Top + 16))
            ?? new Point(area.Left + 16, area.Top + 16);
        gameOverlay.Left = origin.X;
        gameOverlay.Top = origin.Y;
    }

    private void CloseOverlay()
    {
        gameOverlay?.Close();
        gameOverlay = null;
        overlayContent = null;
    }

    private void QuietGameChanged(object sender, RoutedEventArgs e)
    {
        if (gameSettingsLoaded) TrySave("quiet-game.json", QuietGame.IsChecked == true);
    }

    private bool QueueGameAlert(string message)
    {
        if (match is null || QuietGame.IsChecked != true) return false;
        if (quietAlerts.Count < 12) quietAlerts.Add(message);
        return true;
    }

    private void FlushQuietGame()
    {
        if (quietAlerts.Count == 0) return;
        var icon = Tray();
        icon.Visible = true;
        icon.ShowBalloonTip(5000, "Сводка после игры", string.Join("\n", quietAlerts), System.Windows.Forms.ToolTipIcon.Info);
        quietAlerts.Clear();
    }

    private void ShowWindowsHistory(IReadOnlyList<InventoryObservation> observations)
    {
        var recent = observations.Where(item => item.ObservedAt >= DateTimeOffset.Now.AddDays(-1)).Take(5).ToArray();
        WindowsHistory.Text = recent.Length > 0
            ? $"Сравнений с изменениями за последние сутки · {recent.Length}{(recent.Length == 5 ? "+" : "")}"
            : "За последние сутки изменений в сохранённых сравнениях нет.";
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
}
