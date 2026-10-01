using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Core.Categorization;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;
using Pat.App.Services;
using Pat.App.Tracking;

namespace Pat.App.ViewModels;

public sealed class TimelineItemVm
{
    public required double Left { get; init; }
    public required double Width { get; init; }
    public required Brush Fill { get; init; }
    public required string Tooltip { get; init; }
}

public sealed class HourTickVm
{
    public required double Left { get; init; }
    public required string Label { get; init; }
}

public sealed class TotalRowVm
{
    public required string Name { get; init; }
    public required string Time { get; init; }
    public required Brush Color { get; init; }
    public required double BarFraction { get; init; }
}

public sealed class ProcessEventVm
{
    public required string Time { get; init; }
    public required string Action { get; init; }
    public required Brush ActionBrush { get; init; }
    public required string Name { get; init; }
    public required string Parent { get; init; }
}

public sealed class FileEventVm
{
    public required string Time { get; init; }
    public required string Action { get; init; }
    public required Brush ActionBrush { get; init; }
    public required string FileName { get; init; }
    public required string Directory { get; init; }
    public required string FullPath { get; init; }
}

public sealed class BrowserVisitVm
{
    public required string Time { get; init; }
    public required string Domain { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Browser { get; init; }
}

public sealed partial class TodayViewModel(
    SessionRepository sessions,
    EventRepository events,
    SessionMaterializer materializer,
    SettingsStore settingsStore,
    TrackingService tracking,
    FileActivityCollector fileCollector,
    Services.BrowserIntegrationService browserIntegration,
    Services.RetentionService retention,
    Services.ExportService export) : ObservableObject
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;

    private List<SessionRow> _dayRows = [];
    private DayReport _report = DayReport.Empty(0, 0);

    [ObservableProperty] private string _dateLabel = "";
    [ObservableProperty] private string _activeLabel = "0с";
    [ObservableProperty] private string _idleLabel = "0с";
    [ObservableProperty] private string _pauseLabel = "Пауза";
    [ObservableProperty] private double _timelineWidth = 600;

    [ObservableProperty] private ObservableCollection<TimelineItemVm> _timelineItems = [];
    [ObservableProperty] private ObservableCollection<HourTickVm> _hourTicks = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _categories = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _apps = [];
    [ObservableProperty] private ObservableCollection<ProcessEventVm> _processEvents = [];
    [ObservableProperty] private int _processStartsToday;

    [ObservableProperty] private string _fileCountsLabel = "";
    [ObservableProperty] private ObservableCollection<FileEventVm> _fileEvents = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _fileTopDirs = [];

    [ObservableProperty] private ObservableCollection<BrowserVisitVm> _browserVisits = [];
    [ObservableProperty] private ObservableCollection<TotalRowVm> _topDomains = [];

    /// <summary>Сэмплы CPU/RAM за сегодня — отрисовываются в code-behind через ScottPlot.</summary>
    public List<EventRepository.MetricSampleEntry> MetricSamples { get; private set; } = [];

    /// <summary>Графики нужно перерисовать (вызывается после Refresh).</summary>
    public event Action? PlotsStale;

    // Настройки
    [ObservableProperty] private int _idleThresholdMinutes = 5;
    [ObservableProperty] private string _excludedText = "";
    [ObservableProperty] private string _watchedRootsText = "";
    [ObservableProperty] private string _extensionId = "";
    [ObservableProperty] private bool _autoStartEnabled;
    [ObservableProperty] private int _retentionDays = 180;

    public void Initialize()
    {
        var s = settingsStore.Settings;
        IdleThresholdMinutes = Math.Max(1, s.IdleThresholdSeconds / 60);
        ExcludedText = string.Join(Environment.NewLine, s.ExcludedProcesses);
        WatchedRootsText = string.Join(Environment.NewLine, s.WatchedRoots);
        ExtensionId = s.BrowserExtensionId ?? "";
        AutoStartEnabled = AutoStart.IsEnabled();
        RetentionDays = s.RetentionDays;

        tracking.StateChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdatePauseLabel);
        UpdatePauseLabel();
    }

    private void UpdatePauseLabel() =>
        PauseLabel = tracking.State == TrackingState.Running ? "Пауза" : "Продолжить";

    [RelayCommand]
    private void TogglePause() => tracking.TogglePause();

    [RelayCommand]
    public void Refresh()
    {
        var (dayStart, dayEnd) = LocalDay.Today();
        var rules = materializer.CurrentRules();
        var rows = sessions.GetRange(dayStart, dayEnd, rules);

        _dayRows = rows;
        var segments = rows.Select(ToSegment).ToList();
        _report = new DayReportBuilder().Build(segments, dayStart, dayEnd, rules);

        DateLabel = $"Сегодня, {DateTime.Now: d MMMM}";
        ActiveLabel = TimeText.Humanize(_report.Active);
        IdleLabel = TimeText.Humanize(_report.Idle);

        LoadProcesses(dayStart, dayEnd);
        LoadFiles(dayStart, dayEnd);
        LoadBrowser(dayStart, dayEnd);
        MetricSamples = events.GetMetricSamples(dayStart, dayEnd);

        RebuildTimeline();
        RebuildTotals();
        PlotsStale?.Invoke();
    }

    private void LoadProcesses(long dayStart, long dayEnd)
    {
        ProcessStartsToday = (int)events.CountProcessStarts(dayStart, dayEnd);
        ProcessEvents = new ObservableCollection<ProcessEventVm>(
            events.GetProcessEvents(dayStart, dayEnd, 12).Select(e => new ProcessEventVm
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(e.TsUtcMs).ToLocalTime().ToString("HH:mm:ss"),
                Action = e.Started ? "▲ запущен" : "■ завершён",
                ActionBrush = e.Started
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B92A3")),
                Name = e.ExeName,
                Parent = e.ParentName is { } parent ? $"← {parent}" : "",
            }));
    }

    private void LoadFiles(long dayStart, long dayEnd)
    {
        var (created, modified, deleted, renamed) = events.CountFileEvents(dayStart, dayEnd);
        FileCountsLabel = created + modified + deleted + renamed == 0
            ? "нет событий"
            : $"создано {created} · изменено {modified} · удалено {deleted} · переименовано {renamed}";

        var recent = events.GetFileEvents(dayStart, dayEnd, 12);
        FileEvents = new ObservableCollection<FileEventVm>(recent.Select(e =>
        {
            var name = System.IO.Path.GetFileName(e.Path);
            if (e.Kind == Pat.Core.Events.ActivityEventKind.FileRenamed && e.OldPath is { } old)
                name = $"{System.IO.Path.GetFileName(old)} → {name}";
            return new FileEventVm
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(e.TsUtcMs).ToLocalTime().ToString("HH:mm:ss"),
                Action = e.Kind switch
                {
                    Pat.Core.Events.ActivityEventKind.FileCreated => "▲ создан",
                    Pat.Core.Events.ActivityEventKind.FileModified => "✎ изменён",
                    Pat.Core.Events.ActivityEventKind.FileDeleted => "✖ удалён",
                    Pat.Core.Events.ActivityEventKind.FileRenamed => "⇄ переименован",
                    _ => "?",
                },
                ActionBrush = e.Kind switch
                {
                    Pat.Core.Events.ActivityEventKind.FileCreated => GreenBrush,
                    Pat.Core.Events.ActivityEventKind.FileModified => BlueBrush,
                    Pat.Core.Events.ActivityEventKind.FileRenamed => AmberBrush,
                    _ => DimBrush,
                },
                FileName = name,
                Directory = System.IO.Path.GetDirectoryName(e.Path) ?? "",
                FullPath = e.OldPath is { } oldPath ? $"{oldPath} → {e.Path}" : e.Path,
            };
        }));

        var topDirs = events.GetFileEvents(dayStart, dayEnd, 400)
            .Select(e => System.IO.Path.GetDirectoryName(e.Path) ?? "")
            .Where(d => d.Length > 0)
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();
        var maxDirCount = topDirs.Count > 0 ? topDirs[0].Count() : 1;
        FileTopDirs = new ObservableCollection<TotalRowVm>(topDirs.Select(g => new TotalRowVm
        {
            Name = g.Key,
            Time = g.Count().ToString(),
            Color = AmberBrush,
            BarFraction = g.Count() / (double)maxDirCount,
        }));
    }

    private void LoadBrowser(long dayStart, long dayEnd)
    {
        var visits = events.GetBrowserVisits(dayStart, dayEnd, 400);
        BrowserVisits = new ObservableCollection<BrowserVisitVm>(visits.Take(12).Select(v => new BrowserVisitVm
        {
            Time = DateTimeOffset.FromUnixTimeMilliseconds(v.TsUtcMs).ToLocalTime().ToString("HH:mm:ss"),
            Domain = v.Domain,
            Title = v.Title.Length > 80 ? v.Title[..80] + "…" : v.Title,
            Url = v.Url,
            Browser = v.Browser,
        }));

        TopDomains = new ObservableCollection<TotalRowVm>(visits
            .GroupBy(v => v.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => new TotalRowVm
            {
                Name = g.Key,
                Time = g.Count().ToString(),
                Color = PurpleBrush,
                BarFraction = visits.Count > 0 ? g.Count() / (double)visits.Count : 0,
            }));
    }

    private static readonly Brush GreenBrush = BrushFromHex("#10B981");
    private static readonly Brush BlueBrush = BrushFromHex("#3B82F6");
    private static readonly Brush AmberBrush = BrushFromHex("#F59E0B");
    private static readonly Brush PurpleBrush = BrushFromHex("#8B5CF6");
    private static readonly Brush DimBrush = BrushFromHex("#8B92A3");

    private static Brush BrushFromHex(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static ActivitySegment ToSegment(SessionRow r) => new(
        r.StartedUtcMs, r.EndedUtcMs, r.AppPath, r.AppName ?? "(исключено)", r.WindowTitle, r.IsIdle, r.IsExcluded);

    partial void OnTimelineWidthChanged(double value)
    {
        RebuildTimeline();
        RebuildHourTicks();
    }

    private void RebuildTimeline()
    {
        var (dayStart, dayEnd) = LocalDay.Today();
        var span = (double)(dayEnd - dayStart);
        var width = Math.Max(1, TimelineWidth);

        TimelineItems = new ObservableCollection<TimelineItemVm>(
            _dayRows
                .Where(r => r.EndedUtcMs > dayStart && r.StartedUtcMs < dayEnd)
                .Select(r =>
                {
                    var start = Math.Max(r.StartedUtcMs, dayStart);
                    var end = Math.Min(r.EndedUtcMs, dayEnd);
                    var startLocal = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime();
                    var endLocal = DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime();
                    var name = r.IsIdle ? "Простой" : r.IsExcluded ? "(исключено)" : r.AppName;
                    var detail = r.WindowTitle is { Length: > 0 } && !r.IsIdle ? $" — {r.WindowTitle}" : "";
                    return new TimelineItemVm
                    {
                        Left = (start - dayStart) / span * width,
                        Width = Math.Max(1.5, (end - start) / span * width),
                        Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.CategoryColorHex)),
                        Tooltip = $"{startLocal:HH:mm}–{endLocal:HH:mm}  {name}{detail}",
                    };
                }));
    }

    private void RebuildHourTicks()
    {
        var width = Math.Max(1, TimelineWidth);
        HourTicks = new ObservableCollection<HourTickVm>(
            Enumerable.Range(0, 25).Select(h => new HourTickVm
            {
                Left = h / 24.0 * width - (h % 6 == 0 ? 10 : 6),
                Label = h.ToString(),
            }));
    }

    private void RebuildTotals()
    {
        var totalMs = _report.Active.TotalMilliseconds;
        Categories = new ObservableCollection<TotalRowVm>(
            _report.Categories.Select(c => new TotalRowVm
            {
                Name = c.Name,
                Time = TimeText.Humanize(c.Total),
                Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.ColorHex)),
                BarFraction = totalMs > 0 ? c.Total.TotalMilliseconds / totalMs : 0,
            }));

        Apps = new ObservableCollection<TotalRowVm>(
            _report.Apps.Take(8).Select(a => new TotalRowVm
            {
                Name = a.AppName,
                Time = TimeText.Humanize(a.Total),
                Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(a.CategoryColorHex)),
                BarFraction = totalMs > 0 ? a.Total.TotalMilliseconds / totalMs : 0,
            }));
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var s = settingsStore.Settings;
        s.IdleThresholdSeconds = Math.Max(1, IdleThresholdMinutes) * 60;
        s.ExcludedProcesses = ExcludedText
            .Split([Environment.NewLine, ","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        s.WatchedRoots = WatchedRootsText
            .Split([Environment.NewLine, ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        s.BrowserExtensionId = string.IsNullOrWhiteSpace(ExtensionId) ? null : ExtensionId.Trim();
        s.StartWithWindows = AutoStartEnabled;
        s.RetentionDays = Math.Max(0, RetentionDays);
        settingsStore.Save(s);

        if (AutoStart.IsEnabled() != AutoStartEnabled)
            AutoStart.SetEnabled(AutoStartEnabled);

        // Корни файлового наблюдения применяются без перезапуска трекинга
        fileCollector.Reconfigure();

        // Правила/исключения могли измениться — пересобираем сессии и обновляем экран
        tracking.RefreshSessions();
        Refresh();
    }

    [RelayCommand]
    private void InstallBrowserIntegration()
    {
        var (ok, message) = browserIntegration.Install(ExtensionId);
        System.Windows.MessageBox.Show(message, "Интеграция с браузером",
            System.Windows.MessageBoxButton.OK,
            ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void UninstallBrowserIntegration()
    {
        browserIntegration.Uninstall();
        System.Windows.MessageBox.Show("Интеграция удалена: ключи реестра и манифест хоста очищены.",
            "Интеграция с браузером", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportDay()
    {
        var (dayStart, _) = LocalDay.Today();
        export.ExportRange("activity_day", dayStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [RelayCommand]
    private void PurgeOldData()
    {
        var message = retention.PurgeNow();
        System.Windows.MessageBox.Show(message, "Очистка старых данных",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
