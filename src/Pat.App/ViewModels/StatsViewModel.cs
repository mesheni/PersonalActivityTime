using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Infrastructure.Database;

namespace Pat.App.ViewModels;

public sealed record DaySummaryVm(DateTime Date, string Active, string Idle);

public sealed partial class StatsViewModel(StatsService stats, EventRepository events) : ObservableObject
{
    private readonly LatestLoad _load = new();
    [ObservableProperty] private bool _monthRange;
    [ObservableProperty] private string _rangeLabel = "7 дней";
    [ObservableProperty] private string _activeSummary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<TotalRowVm> _categories = [];
    [ObservableProperty] private ObservableCollection<DaySummaryVm> _daySummaries = [];
    public List<DayTotals> Days { get; private set; } = [];
    public List<EventRepository.MetricSampleEntry> MetricSamples { get; private set; } = [];
    public (long Start, long End)? LoadedRange { get; private set; }
    public event Action? StatsLoaded;
    public event Action<DateTime>? DayRequested;
    [RelayCommand] private async Task SetRangeWeekAsync() { MonthRange = false; await LoadAsync(); }
    [RelayCommand] private async Task SetRangeMonthAsync() { MonthRange = true; await LoadAsync(); }
    [RelayCommand] private void OpenDay(DaySummaryVm? day) { if (day is not null) DayRequested?.Invoke(day.Date); }
    public void CancelLoad() { _load.Cancel(); IsLoading = false; }
    public async Task LoadAsync()
    {
        var request = _load.Begin();
        var days = MonthRange ? 30 : 7;
        var fromDate = DateTime.Today.AddDays(-(days - 1));
        var (start, _) = LocalDay.Bounds(fromDate);
        var today = DateTime.Today;
        var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IsLoading = true;
        Status = "Загрузка статистики…";
        try
        {
            var result = await Task.Run(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                var totals = stats.GetDailyTotals(fromDate, today.AddDays(1));
                request.Token.ThrowIfCancellationRequested();
                return (totals, samples: events.GetMetricSamples(start, end));
            }, request.Token);
            if (!_load.IsCurrent(request)) return;
            Days = result.totals;
            MetricSamples = result.samples;
            LoadedRange = (start, end);
            RangeLabel = $"{days} дней · {fromDate:dd.MM.yyyy}–{today:dd.MM.yyyy}";
            var total = TimeSpan.FromTicks(Days.Sum(d => d.Active.Ticks));
            ActiveSummary = $"Активно за период: {TimeText.Humanize(total)}";
            Categories = new(Days.SelectMany(d => d.Categories).GroupBy(c => c.Name)
                .Select(g => (Name: g.Key, Color: g.First().ColorHex, Total: TimeSpan.FromTicks(g.Sum(c => c.Total.Ticks))))
                .OrderByDescending(c => c.Total).Select(c => new TotalRowVm(c.Name,
                    TimeText.Humanize(c.Total), TodayViewModel.Brush(c.Color), total > TimeSpan.Zero ? c.Total / total : 0)));
            DaySummaries = new(Days.Select(d => new DaySummaryVm(d.LocalDate, TimeText.Humanize(d.Active), TimeText.Humanize(d.Idle))));
            Status = total == TimeSpan.Zero ? "За выбранный период нет записанной активности." : $"Обновлено в {DateTime.Now:HH:mm:ss}";
            StatsLoaded?.Invoke();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (_load.IsCurrent(request)) Status = "Не удалось загрузить статистику: " + ex.Message; }
        finally { if (_load.IsCurrent(request)) IsLoading = false; _load.End(request); }
    }
}
