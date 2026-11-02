using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Core.Reporting;
using Pat.Infrastructure.Database;

namespace Pat.App.ViewModels;

public sealed record TotalRowVm(string Name, string Time, Brush Color, double BarFraction);
public sealed record HourTickVm(double Left, string Label);
public sealed record TimelineItemVm(long Start, double Left, double Width, Brush Fill, string Tooltip);
public sealed record IntervalVm(long Start, string TimeRange, string Duration, string App,
    string Category, string Title, Brush Color)
{
    public string Details => $"{TimeRange} · {Duration}\n{App} · {Category}\n{Title}";
}
public sealed record NarrativeBlockVm(long Start, string TimeRange, string Duration, string Title, Brush Color, string Details);

public sealed partial class TodayViewModel(IDayActivityService activity, DayContext context,
    ReportViewModel assistant) : ObservableObject
{
    private readonly LatestLoad _load = new();
    private DayReport? _report;
    private IReadOnlyList<SessionRow> _rows = [];
    [ObservableProperty] private string _activeLabel = "—";
    [ObservableProperty] private string _idleLabel = "—";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showAllApps;
    [ObservableProperty] private string _appsLabel = "Топ-8 приложений";
    [ObservableProperty] private double _timelineWidth = 600;
    [ObservableProperty] private ObservableCollection<TimelineItemVm> _timelineItems = [];
    [ObservableProperty] private ObservableCollection<HourTickVm> _hourTicks = [];
    [ObservableProperty] private ObservableCollection<IntervalVm> _intervals = [];
    [ObservableProperty] private IntervalVm? _selectedInterval;
    [ObservableProperty] private ObservableCollection<NarrativeBlockVm> _blocks = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _categories = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _apps = [];

    public async Task LoadAsync()
    {
        var request = _load.Begin();
        var date = context.SelectedDate;
        IsLoading = true;
        Status = "Загрузка дня…";
        try
        {
            var day = await activity.ReadAsync(date, request.Token);
            if (!_load.IsCurrent(request) || date != context.SelectedDate) return;
            var selected = SelectedInterval?.Start;
            _report = day.Report;
            _rows = day.Rows;
            ActiveLabel = TimeText.Humanize(day.Report.Active);
            IdleLabel = TimeText.Humanize(day.Report.Idle);
            Headline = day.Narrative.Blocks.Count == 0 ? "В этот день нет записанной активности"
                : $"Самое долгое занятие: {day.Narrative.Blocks.MaxBy(b => b.Duration)!.Title} · " +
                  $"{TimeText.Humanize(day.Narrative.LongestStreak)} без смены занятия";
            CollectionUpdates.Reconcile(Blocks, day.Narrative.Blocks.Select(b => new NarrativeBlockVm(
                b.StartUtcMs, Range(b.StartUtcMs, b.EndUtcMs), TimeText.Humanize(b.Duration), b.Title,
                Brush(b.ColorHex), string.Join(" · ", b.Details))), b => b.Start);
            CollectionUpdates.Reconcile(Intervals, day.Rows.Select(r => new IntervalVm(r.StartedUtcMs,
                Range(r.StartedUtcMs, r.EndedUtcMs), TimeText.Humanize(TimeSpan.FromMilliseconds(r.EndedUtcMs - r.StartedUtcMs)),
                r.IsIdle ? "Простой" : r.IsExcluded ? "Исключённое приложение" : r.AppName ?? "Неизвестное приложение",
                r.IsIdle ? "Простой" : r.IsExcluded ? "Исключено" : r.CategoryName,
                r.IsExcluded ? "Заголовок не записывается" : r.WindowTitle ?? "", Brush(r.CategoryColorHex))), i => i.Start);
            SelectedInterval = Intervals.FirstOrDefault(i => i.Start == selected);
            Categories = new(day.Report.Categories.Select(c => new TotalRowVm(c.Name,
                TimeText.Humanize(c.Total), Brush(c.ColorHex), Fraction(c.Total))));
            RebuildApps();
            RebuildTimeline();
            assistant.SetDay(day);
            Status = day.Rows.Count == 0 ? "Нет записей. Для текущего дня проверьте состояние записи в шапке."
                : $"Обновлено в {DateTime.Now:HH:mm:ss} · интервалов: {day.Rows.Count}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (_load.IsCurrent(request)) Status = "Не удалось загрузить день: " + ex.Message; }
        finally
        {
            if (_load.IsCurrent(request)) IsLoading = false;
            _load.End(request);
        }
    }

    public void CancelLoad() { _load.Cancel(); IsLoading = false; }
    public void ClearDay()
    {
        CancelLoad();
        _report = null;
        _rows = [];
        ActiveLabel = IdleLabel = "—";
        Headline = "";
        Blocks = []; Intervals = []; Categories = []; Apps = []; TimelineItems = [];
        SelectedInterval = null;
        assistant.InvalidateDay();
    }
    private double Fraction(TimeSpan duration) => _report?.Active > TimeSpan.Zero
        ? duration.TotalMilliseconds / _report.Active.TotalMilliseconds : 0;
    private void RebuildApps()
    {
        if (_report is null) return;
        Apps = new((ShowAllApps ? _report.Apps : _report.Apps.Take(8)).Select(a =>
            new TotalRowVm(a.AppName, TimeText.Humanize(a.Total), Brush(a.CategoryColorHex), Fraction(a.Total))));
        AppsLabel = ShowAllApps ? $"Все приложения ({_report.Apps.Count})" : $"Топ-8 приложений · всего {_report.Apps.Count}";
    }
    partial void OnShowAllAppsChanged(bool value) => RebuildApps();
    partial void OnTimelineWidthChanged(double value) => RebuildTimeline();
    private void RebuildTimeline()
    {
        var (start, end) = LocalDay.Bounds(context.SelectedDate);
        var width = Math.Max(1, TimelineWidth);
        var span = (double)(end - start);
        TimelineItems = new(_rows.Select(r => new TimelineItemVm(r.StartedUtcMs,
            (r.StartedUtcMs - start) / span * width,
            Math.Min(width - (r.StartedUtcMs - start) / span * width,
                Math.Max(1, (r.EndedUtcMs - r.StartedUtcMs) / span * width)),
            Brush(r.CategoryColorHex), $"{Range(r.StartedUtcMs, r.EndedUtcMs)} · {r.CategoryName}")));
        var hours = (int)(span / 3_600_000);
        HourTicks = new(Enumerable.Range(0, hours + 1).Where(h => h % 3 == 0 || h == hours)
            .Select(h => new HourTickVm(Math.Clamp(h / (double)hours * width - 8, 0, Math.Max(0, width - 36)),
                h == hours ? "24:00" : DateTimeOffset.FromUnixTimeMilliseconds(start + h * 3_600_000L).ToLocalTime().ToString("HH:mm"))));
    }
    [RelayCommand] private void SelectSegment(TimelineItemVm? segment) =>
        SelectedInterval = Intervals.FirstOrDefault(i => i.Start == segment?.Start);
    public static string Range(long start, long end)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime();
        var to = DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime();
        var endLabel = to.Date > from.Date && to.TimeOfDay == TimeSpan.Zero ? "24:00" : to.ToString("HH:mm");
        return $"{from:HH:mm}–{endLabel}";
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> Brushes = new();
    public static Brush Brush(string hex) => Brushes.GetOrAdd(hex, value =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze(); return brush;
    });
}
