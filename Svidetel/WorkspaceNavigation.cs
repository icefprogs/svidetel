using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Svidetel;

public partial class MainWindow
{
    private sealed record PageLink(string Label, TabItem Tab);
    private PageLink[] gamePages = [];
    private PageLink[] windowsPages = [];
    private TabItem? lastGamePage, lastWindowsPage;
    private bool updatingNavigation;

    private void InitializeNavigation()
    {
        gamePages = [new("Сессия", GamerTab), new("Библиотека Steam", LibraryTab), new("Разбор фризов", ReviewTab)];
        windowsPages = [new("Сканирование", ScanTab), new("Защита", DefenderTab),
            new("Восстановление", HistoryTab), new("Изменения и охват", CoverageTab)];
        lastGamePage = GamerTab;
        lastWindowsPage = ScanTab;
        SyncNavigation();
    }

    private void NavigateClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, GamesNav)) Sections.SelectedItem = lastGamePage;
        else if (ReferenceEquals(sender, NetworkNav)) Sections.SelectedItem = ServersTab;
        else if (ReferenceEquals(sender, WindowsNav)) Sections.SelectedItem = lastWindowsPage;
        else if (ReferenceEquals(sender, SettingsNav)) Sections.SelectedItem = SettingsTab;
        else Sections.SelectedItem = HomeTab;
    }

    private void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Sections) && gamePages.Length > 0) SyncNavigation();
    }

    private void SubnavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingNavigation && gamePages.Length > 0 && Subnav.SelectedItem is PageLink link)
            Sections.SelectedItem = link.Tab;
    }

    private void SyncNavigation()
    {
        var page = Sections.SelectedItem as TabItem;
        var game = gamePages.FirstOrDefault(link => link.Tab == page);
        var windows = windowsPages.FirstOrDefault(link => link.Tab == page);
        updatingNavigation = true;
        if (game is not null)
        {
            GamesNav.IsChecked = true;
            lastGamePage = game.Tab;
            ShowSubnav(gamePages, game);
        }
        else if (windows is not null)
        {
            WindowsNav.IsChecked = true;
            lastWindowsPage = windows.Tab;
            ShowSubnav(windowsPages, windows);
        }
        else
        {
            if (page == ServersTab) NetworkNav.IsChecked = true;
            else if (page == SettingsTab) SettingsNav.IsChecked = true;
            else OverviewNav.IsChecked = true;
            Subnavigation.Visibility = Visibility.Collapsed;
        }
        updatingNavigation = false;
    }

    private void ShowSubnav(PageLink[] pages, PageLink selected)
    {
        if (!ReferenceEquals(Subnav.ItemsSource, pages)) Subnav.ItemsSource = pages;
        Subnav.SelectedItem = selected;
        Subnavigation.Visibility = Visibility.Visible;
    }

    private void OpenLibraryClick(object sender, RoutedEventArgs e) => Sections.SelectedItem = LibraryTab;
    private void QuickSearchClick(object sender, RoutedEventArgs e) => OpenQuickSearch();

    private void OpenLatestReportClick(object sender, RoutedEventArgs e)
    {
        if (matchHistory.FirstOrDefault() is { } latest) OpenHistoricalReport(latest);
        else Sections.SelectedItem = ReviewTab;
    }

    private void ShowHomeReport(MatchResult? report, bool example = false)
    {
        var available = report is not null;
        HomeEmpty.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        HomeReport.Visibility = HomeReviewButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        HomeLatest.Text = available
            ? $"{(example ? "Демо · " : "")}{Path.GetFileNameWithoutExtension(report!.Game)} · {report.At:g}"
            : "";
        if (report is null) return;

        HomeFps.Text = Format(report.Fps);
        HomeLow.Text = Format(report.Low1);
        HomeP99.Text = Format(report.P99);
        var peak = report.Points.Select(point => point.FrameMs).Max();
        var hasChart = report.Points.Count(point => point.FrameMs.HasValue) >= 2 && peak is > 0;
        HomeChart.Visibility = hasChart ? Visibility.Visible : Visibility.Collapsed;
        HomeFrameEmpty.Visibility = hasChart ? Visibility.Collapsed : Visibility.Visible;
        HomeFrameEmpty.Text = "В этой записи нет достаточных данных PresentMon для графика.";
        var marks = report.Marks.Length;
        HomeFrameNote.Text = report.Frames > 0
            ? $"{report.Frames:N0} кадров · Отметок фризов {marks}" + (report.Limited ? " · статистика FPS ограничена объёмом записи" : "")
            : $"Замеров CPU и сети {report.Points.Length:N0} · Отметок {marks}";
        if (!hasChart) return;

        var end = Math.Max(1, report.Points[^1].Second);
        var top = Math.Max(20, Math.Ceiling(peak!.Value / 20) * 20);
        HomeScaleHigh.Text = $"{top:0} мс";
        HomeScaleMid.Text = $"{top / 2:0} мс";
        HomeTimeEnd.Text = TimeSpan.FromSeconds(end).ToString(end >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        HomeFrameLine.Data = Trace(report.Points, point => point.FrameMs, 0, end, top);
        var markerLines = new GeometryGroup();
        foreach (var mark in report.Marks.Where(mark => mark <= end))
        {
            var x = 700 * mark / end;
            markerLines.Children.Add(new LineGeometry(new Point(x, 2), new Point(x, 139)));
        }
        HomeFreezeLines.Data = markerLines;
    }

    private static PathGeometry Trace(MatchPoint[] data, Func<MatchPoint, double?> value, double start, double end, double minimum)
    {
        var result = new PathGeometry();
        var scale = Math.Max(minimum, data.Select(value).Max() ?? 0);
        PathFigure? line = null;
        foreach (var point in data)
        {
            if (value(point) is not { } sample) { line = null; continue; }
            var coordinate = new Point(700 * (point.Second - start) / (end - start), 139 - 137 * sample / scale);
            if (line is null)
            {
                line = new PathFigure { StartPoint = coordinate };
                result.Figures.Add(line);
            }
            else line.Segments.Add(new LineSegment(coordinate, true));
        }
        return result;
    }
}
