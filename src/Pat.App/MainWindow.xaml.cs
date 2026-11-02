using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pat.App.Tracking;
using Pat.App.ViewModels;
using Pat.Infrastructure.Database;
using ScottPlot;

namespace Pat.App;

/// <summary>Конвертеры для XAML.</summary>
public sealed class BarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (values is [double fraction, double containerWidth] && containerWidth > 0 && fraction > 0)
            return Math.Max(4, fraction * (containerWidth - 40));
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmptyListToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is System.Collections.ICollection { Count: 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Непустая строка → Visible (для показа ответа LLM).</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → !bool (кнопка отключена, пока идёт запрос).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is bool b ? !b : true;
}

public partial class MainWindow : Window
{
    private readonly TodayViewModel _viewModel;
    private readonly ReportViewModel _reportViewModel;
    private readonly StatsViewModel _statsViewModel;
    private readonly TrackingService _tracking;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;

    public MainWindow(TodayViewModel viewModel, ReportViewModel reportViewModel, StatsViewModel statsViewModel, TrackingService tracking)
    {
        _viewModel = viewModel;
        _reportViewModel = reportViewModel;
        _statsViewModel = statsViewModel;
        _tracking = tracking;
        InitializeComponent();
        DataContext = viewModel;
        ReportRoot.DataContext = reportViewModel;
        StatsRoot.DataContext = statsViewModel;
        var icon = App.CreateAppIcon();
        Icon = icon;
        TrayIcon.IconSource = icon;

        viewModel.Initialize();
        reportViewModel.Initialize();
        viewModel.PlotsStale += RenderTodayPlots;
        statsViewModel.StatsLoaded += RenderStatsPlots;

        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _refreshTimer.Tick += (_, _) => viewModel.Refresh();
        _refreshTimer.Start();

        Loaded += (_, _) => viewModel.Refresh();
    }

    private void TimelineCanvas_Loaded(object sender, RoutedEventArgs e) =>
        UpdateTimelineWidth(sender);

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTimelineWidth(sender);

    private void UpdateTimelineWidth(object sender)
    {
        if (sender is Canvas canvas)
            _viewModel.TimelineWidth = Math.Max(50, canvas.ActualWidth);
    }

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        try
        {
            if (ReportTab.IsSelected) _reportViewModel.Load();
            else if (StatsTab.IsSelected) await _statsViewModel.LoadAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка загрузки отчёта"); }
    }

    // ==================== Отрисовка ScottPlot ====================

    private static void StylePlot(Plot plot)
    {
        plot.FigureBackground = new BackgroundStyle { Color = Color.FromHex("#242730") };
        plot.DataBackground = new BackgroundStyle { Color = Color.FromHex("#242730") };
        plot.Axes.Color(Color.FromHex("#8B92A3"));
        plot.Grid.MajorLineColor = Color.FromHex("#333744");
        plot.Legend.BackgroundColor = Color.FromHex("#242730");
        plot.Legend.FontColor = Color.FromHex("#E6E8EE");
        plot.Legend.FontSize = 11;
    }

    private void RenderTodayPlots() => RenderMetricsPlot(TodayMetricsPlot, _viewModel.MetricSamples);

    private void RenderStatsPlots()
    {
        RenderDailyBars(DailyActivityPlot, _statsViewModel.Days);
        RenderMetricsPlot(RangeMetricsPlot, _statsViewModel.MetricSamples);
    }

    /// <summary>Линии CPU% и RAM% по сэмплам (ось X — время).</summary>
    private static void RenderMetricsPlot(ScottPlot.WPF.WpfPlot control, IReadOnlyList<EventRepository.MetricSampleEntry> samples)
    {
        var plot = control.Plot;
        plot.Clear();
        StylePlot(plot);

        if (samples.Count >= 2)
        {
            var xs = samples
                .Select(s => DateTimeOffset.FromUnixTimeMilliseconds(s.TsUtcMs).ToLocalTime().DateTime.ToOADate())
                .ToArray();

            var cpu = plot.Add.Scatter(xs, samples.Select(s => s.CpuPercent).ToArray());
            cpu.LegendText = "CPU %";
            cpu.LineColor = Color.FromHex("#3B82F6");
            cpu.LineWidth = 1.5f;
            cpu.MarkerSize = 0;

            var ram = plot.Add.Scatter(xs, samples.Select(s => s.RamPercent).ToArray());
            ram.LegendText = "RAM %";
            ram.LineColor = Color.FromHex("#F59E0B");
            ram.LineWidth = 1.5f;
            ram.MarkerSize = 0;

            plot.Axes.DateTimeTicksBottom();
            plot.Axes.SetLimitsY(0, 100);
            plot.ShowLegend();
        }
        else
        {
            plot.Axes.SetLimitsY(0, 100);
        }

        control.Refresh();
    }

    /// <summary>Stacked bars: часы активности по дням, серии — топ-категории + «Прочее».</summary>
    private static void RenderDailyBars(ScottPlot.WPF.WpfPlot control, IReadOnlyList<DayTotals> days)
    {
        var plot = control.Plot;
        plot.Clear();
        StylePlot(plot);

        if (days.Count == 0)
        {
            control.Refresh();
            return;
        }

        var categories = days
            .SelectMany(d => d.Categories)
            .GroupBy(c => c.Name)
            .Select(g => (Name: g.Key, ColorHex: g.First().ColorHex,
                TotalHours: g.Sum(c => c.Total.TotalHours)))
            .OrderByDescending(c => c.TotalHours)
            .ToList();

        var top = categories.Take(5).ToList();
        var restNames = categories.Skip(5).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var bases = new double[days.Count];

        void AddSeries(string name, string colorHex, Func<DayTotals, double> value)
        {
            var bars = new List<Bar>();
            for (var i = 0; i < days.Count; i++)
            {
                var v = value(days[i]);
                if (v <= 0)
                    continue;
                bars.Add(new Bar
                {
                    Position = i,
                    Value = v,
                    ValueBase = bases[i],
                    FillColor = Color.FromHex(colorHex),
                    LineWidth = 0,
                });
                bases[i] += v;
            }

            if (bars.Count > 0)
            {
                var series = plot.Add.Bars(bars.ToArray());
                series.LegendText = name;
            }
        }

        foreach (var cat in top)
        {
            var localCat = cat;
            AddSeries(cat.Name, cat.ColorHex, d =>
                d.Categories.Where(c => c.Name == localCat.Name).Sum(c => c.Total.TotalHours));
        }

        if (restNames.Count > 0)
        {
            AddSeries("Прочее", "#94A3B8", d =>
                d.Categories.Where(c => restNames.Contains(c.Name)).Sum(c => c.Total.TotalHours));
        }

        var positions = Enumerable.Range(0, days.Count).Select(i => (double)i).ToArray();
        var labels = days.Select(d => d.LocalDate.ToString("dd.MM")).ToArray();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(positions, labels);
        plot.Axes.SetLimits(-0.7, days.Count - 0.3, 0, Math.Max(1, bases.Max() * 1.1));
        plot.ShowLegend();

        control.Refresh();
    }

    // ==================== Tray / lifecycle ====================

    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        Show();
        Activate();
    }

    private void MenuItem_Open(object sender, RoutedEventArgs e)
    {
        Show();
        Activate();
    }

    private async void MenuItem_TogglePause(object sender, RoutedEventArgs e)
    {
        try { await _tracking.TogglePauseAsync(); _viewModel.Refresh(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Трекинг"); }
    }

    private void MenuItem_Exit(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
        Application.Current.Shutdown();
    }

    private bool _allowClose;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Крестик прячет окно в tray; выход — только через меню трея
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _refreshTimer.Stop();
        TrayIcon.Dispose();
        base.OnClosing(e);
    }
}
