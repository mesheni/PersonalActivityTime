using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Infrastructure.Database;

namespace Pat.App.ViewModels;

/// <summary>Вкладка «Статистика»: неделя/месяц, итоги по дням из журнала, метрики за период.</summary>
public sealed partial class StatsViewModel(
    StatsService stats,
    EventRepository events,
    Services.ExportService export) : ObservableObject
{
    [ObservableProperty] private bool _monthRange;
    [ObservableProperty] private string _rangeLabel = "7 дней";
    [ObservableProperty] private string _activeSummary = "";

    public List<DayTotals> Days { get; private set; } = [];

    /// <summary>Топ категорий за период (для списка под графиком).</summary>
    public ObservableCollection<TotalRowVm> Categories { get; private set; } = [];

    public List<EventRepository.MetricSampleEntry> MetricSamples { get; private set; } = [];

    public event Action? StatsLoaded;
    private int _loadVersion;

    [RelayCommand]
    private async Task SetRangeWeekAsync()
    {
        MonthRange = false;
        try { await LoadAsync(); }
        catch (Exception ex) { ActiveSummary = "Не удалось загрузить статистику: " + ex.Message; }
    }

    [RelayCommand]
    private async Task SetRangeMonthAsync()
    {
        MonthRange = true;
        try { await LoadAsync(); }
        catch (Exception ex) { ActiveSummary = "Не удалось загрузить статистику: " + ex.Message; }
    }

    [RelayCommand]
    private void ExportPeriod()
    {
        var days = MonthRange ? 30 : 7;
        var rangeStart = DateTime.Now.Date.AddDays(-(days - 1));
        var (fromUtcMs, _) = LocalDay.Bounds(rangeStart);
        export.ExportRange(MonthRange ? "activity_30d" : "activity_7d",
            fromUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var days = MonthRange ? 30 : 7;
        RangeLabel = MonthRange ? "30 дней" : "7 дней";

        var rangeStart = DateTime.Now.Date.AddDays(-(days - 1));
        var (rangeStartUtc, _) = LocalDay.Bounds(rangeStart);
        var endDate = DateTime.Now.Date.AddDays(1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = await Task.Run(() => (
            Days: stats.GetDailyTotals(rangeStart, endDate),
            Metrics: events.GetMetricSamples(rangeStartUtc, now)));
        if (version != _loadVersion) return;
        Days = snapshot.Days;
        MetricSamples = snapshot.Metrics;

        var totalActive = TimeSpan.FromTicks(Days.Sum(d => d.Active.Ticks));
        ActiveSummary = $"Активно за период: {TimeText.Humanize(totalActive)}";

        var byCategory = Days
            .SelectMany(d => d.Categories)
            .GroupBy(c => c.Name)
            .Select(g => (Name: g.Key, ColorHex: g.First().ColorHex, Total: TimeSpan.FromTicks(g.Sum(c => c.Total.Ticks))))
            .OrderByDescending(c => c.Total)
            .ToList();

        Categories = new ObservableCollection<TotalRowVm>(byCategory.Select(c => new TotalRowVm
        {
            Name = c.Name,
            Time = TimeText.Humanize(c.Total),
            Color = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(c.ColorHex)),
            BarFraction = totalActive > TimeSpan.Zero
                ? c.Total.Ticks / (double)totalActive.Ticks
                : 0,
        }));

        OnPropertyChanged(nameof(Categories));
        StatsLoaded?.Invoke();
    }
}
