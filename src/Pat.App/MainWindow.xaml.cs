using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Pat.App.ViewModels;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;
using ScottPlot;

namespace Pat.App;

public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
public sealed class InverseVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly SettingsStore _settings;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private DateTime _lastToday = DateTime.Today;
    private bool _allowClose;
    private bool _trayHintShown;

    public MainWindow(ShellViewModel shell, SettingsStore settings)
    {
        _shell = shell; _settings = settings;
        InitializeComponent();
        DataContext = shell;
        TrayMenu.DataContext = shell;
        Icon = App.CreateAppIcon();
        TrayIcon.IconSource = Icon;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.TrackingLabel)) UpdateTray();
        };
        shell.Journal.MetricsLoaded += () => RenderMetricsPlot(TodayMetricsPlot, shell.Journal.MetricSamples);
        shell.Stats.StatsLoaded += () =>
        {
            RenderDailyBars(DailyActivityPlot, shell.Stats.Days);
            RenderMetricsPlot(RangeMetricsPlot, shell.Stats.MetricSamples);
        };
        _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Loaded += async (_, _) => { UpdateTray(); await shell.RefreshVisibleAsync(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible && IsLoaded) await shell.RefreshVisibleAsync(); };
    }

    private void UpdateTray() => TrayIcon.ToolTipText = "Personal Activity Time · " + _shell.TrackingLabel;
    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        var followedToday = _shell.Day.SelectedDate == _lastToday;
        if (_lastToday != DateTime.Today)
        {
            _lastToday = DateTime.Today;
            _shell.Day.UpdateToday();
            if (followedToday) { _shell.Day.SelectedDate = _lastToday; return; }
        }
        if (IsVisible && ((_shell.ShowsDay && _shell.Day.IsToday) || _shell.SelectedSection == 2))
            await _shell.RefreshVisibleAsync();
    }
    private void TimelineCanvas_Loaded(object sender, RoutedEventArgs e) => UpdateTimelineWidth(sender);
    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineWidth(sender);
    private void UpdateTimelineWidth(object sender)
    {
        if (sender is Canvas canvas) _shell.Overview.TimelineWidth = Math.Max(50, canvas.ActualWidth);
    }
    private void DailyPlot_OpenDay(object sender, MouseButtonEventArgs e)
    {
        if (_shell.Stats.IsLoading || _shell.Stats.Days.Count == 0) return;
        var pixel = DailyActivityPlot.GetPlotPixelPosition(e);
        var coordinates = DailyActivityPlot.Plot.GetCoordinates(pixel);
        var index = (int)Math.Round(coordinates.X);
        if (coordinates.Y < 0 || index < 0 || index >= _shell.Stats.Days.Count || Math.Abs(coordinates.X - index) > .5) return;
        _shell.OpenDay(_shell.Stats.Days[index].LocalDate);
    }
    private void CopyJournalDetails(object sender, RoutedEventArgs e)
    {
        try { if (!string.IsNullOrEmpty(JournalDetails.Text)) Clipboard.SetText(JournalDetails.Text); }
        catch (Exception ex) { _shell.OperationStatus = "Не удалось скопировать: " + ex.Message; }
    }

    private static void StylePlot(Plot plot)
    {
        plot.FigureBackground = new BackgroundStyle { Color = Color.FromHex("#242730") };
        plot.DataBackground = new BackgroundStyle { Color = Color.FromHex("#242730") };
        plot.Axes.Color(Color.FromHex("#ADB4C4"));
        plot.Grid.MajorLineColor = Color.FromHex("#333744");
        plot.Legend.BackgroundColor = Color.FromHex("#242730");
        plot.Legend.FontColor = Color.FromHex("#E6E8EE");
        plot.Legend.FontSize = 11;
    }
    private static void RenderMetricsPlot(ScottPlot.WPF.WpfPlot control, IReadOnlyList<EventRepository.MetricSampleEntry> samples)
    {
        var plot = control.Plot;
        plot.Clear(); StylePlot(plot);
        if (samples.Count >= 2)
        {
            var xs = samples.Select(s => DateTimeOffset.FromUnixTimeMilliseconds(s.TsUtcMs).ToLocalTime().DateTime.ToOADate()).ToArray();
            var cpu = plot.Add.Scatter(xs, samples.Select(s => s.CpuPercent).ToArray());
            cpu.LegendText = "CPU %"; cpu.LineColor = Color.FromHex("#60A5FA"); cpu.LineWidth = 1.5f; cpu.MarkerSize = 0;
            var ram = plot.Add.Scatter(xs, samples.Select(s => s.RamPercent).ToArray());
            ram.LegendText = "RAM %"; ram.LineColor = Color.FromHex("#FBBF24"); ram.LineWidth = 1.5f; ram.MarkerSize = 0;
            plot.Axes.DateTimeTicksBottom(); plot.ShowLegend();
        }
        plot.Axes.SetLimitsY(0, 100); control.Refresh();
    }
    private static void RenderDailyBars(ScottPlot.WPF.WpfPlot control, IReadOnlyList<DayTotals> days)
    {
        var plot = control.Plot;
        plot.Clear(); StylePlot(plot);
        if (days.Count == 0) { control.Refresh(); return; }
        var categories = days.SelectMany(d => d.Categories).GroupBy(c => c.Name)
            .Select(g => (Name: g.Key, Color: g.First().ColorHex, Hours: g.Sum(c => c.Total.TotalHours)))
            .OrderByDescending(c => c.Hours).ToList();
        var bases = new double[days.Count];
        void AddSeries(string name, string color, Func<DayTotals, double> value)
        {
            var bars = new List<Bar>();
            for (var i = 0; i < days.Count; i++)
            {
                var v = value(days[i]);
                if (v <= 0) continue;
                bars.Add(new Bar { Position = i, Value = v, ValueBase = bases[i], FillColor = Color.FromHex(color), LineWidth = 0 });
                bases[i] += v;
            }
            if (bars.Count > 0) plot.Add.Bars(bars.ToArray()).LegendText = name;
        }
        foreach (var cat in categories.Take(5))
            AddSeries(cat.Name, cat.Color, d => d.Categories.Where(c => c.Name == cat.Name).Sum(c => c.Total.TotalHours));
        var rest = categories.Skip(5).Select(c => c.Name).ToHashSet();
        if (rest.Count > 0) AddSeries("Прочее", "#94A3B8", d => d.Categories.Where(c => rest.Contains(c.Name)).Sum(c => c.Total.TotalHours));
        // Keep 30-day labels readable at the minimum window width.
        var indices = Enumerable.Range(0, days.Count).Where(i => days.Count <= 7 || i % 3 == 0 || i == days.Count - 1).ToArray();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(indices.Select(i => (double)i).ToArray(), indices.Select(i => days[i].LocalDate.ToString("dd.MM")).ToArray());
        plot.Axes.SetLimits(-.7, days.Count - .3, 0, Math.Max(1, bases.Max() * 1.1));
        plot.ShowLegend(); control.Refresh();
    }
    private void OpenWindow()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
    }
    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e) => OpenWindow();
    private void MenuItem_Open(object sender, RoutedEventArgs e) { _shell.SelectedSection = 0; OpenWindow(); }
    private void MenuItem_Exit(object sender, RoutedEventArgs e)
    {
        if (_shell.Settings.IsDirty && MessageBox.Show("Выйти без сохранения изменений настроек?", "Несохранённые изменения",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _allowClose = true; Close(); Application.Current.Shutdown();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            var settings = _settings.Settings;
            if (!settings.HasSeenTrayHint && !_trayHintShown)
            {
                _trayHintShown = true;
                MessageBox.Show("Окно будет скрыто, а запись активности продолжится. Открыть приложение и завершить его можно через значок в области уведомлений. Команда «Выйти» также доступна в окне.",
                    "Приложение продолжает работать", MessageBoxButton.OK, MessageBoxImage.Information);
                settings.HasSeenTrayHint = true;
                var result = _settings.TrySave(settings);
                if (!result.IsSuccess) _shell.OperationStatus = result.Message;
            }
            Hide(); return;
        }
        _refreshTimer.Stop(); _shell.Overview.CancelLoad(); _shell.Journal.CancelLoad(); _shell.Stats.CancelLoad();
        _shell.Assistant.AskCommand.Cancel(); TrayIcon.Dispose(); base.OnClosing(e);
    }
}
