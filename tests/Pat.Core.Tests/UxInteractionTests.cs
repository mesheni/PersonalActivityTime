using System.Text.Json;
using Pat.App.Services;
using Pat.App.ViewModels;
using Pat.Core.Events;
using Pat.Core.Narrative;
using Pat.Core.Reporting;
using Pat.Infrastructure;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;
using Pat.Infrastructure.Collectors;
using Pat.App.Tracking;

namespace Pat.Core.Tests;

public sealed class UxInteractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pat-ux-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;
    private readonly SqliteConnectionFactory _factory;
    private readonly EventRepository _events;
    private readonly SessionMaterializer _materializer;
    public UxInteractionTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new(_directory);
        _factory = new(Path.Combine(_directory, "activity.db"));
        new MigrationRunner(_factory).Run();
        _events = new(_factory);
        _materializer = new(_events, new SessionRepository(_factory), _settings);
    }

    [Fact]
    public void DayNavigation_NormalizesAndRejectsFutureDates()
    {
        var context = new DayContext();
        context.SelectedDate = DateTime.Today.AddDays(5);
        Assert.Equal(DateTime.Today, context.SelectedDate);
        Assert.False(context.NextDayCommand.CanExecute(null));
        context.PreviousDayCommand.Execute(null);
        Assert.True(context.NextDayCommand.CanExecute(null));
        context.SelectedDate = DateTime.Today.AddDays(-4).AddHours(12);
        Assert.Equal(DateTime.Today.AddDays(-4), context.SelectedDate);
        context.GoTodayCommand.Execute(null);
        Assert.True(context.IsToday);
    }

    [Fact]
    public async Task DayHistory_StatsAndExportAgreeWithoutCachedSessions()
    {
        var date = DateTime.Today.AddDays(-2);
        var (start, end) = LocalDay.Bounds(date);
        _events.AppendBatch([
            ActivityEvent.Foreground(start - 600_000, @"C:\code.exe", "code", "Работа"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, start + 1_800_000),
            ActivityEvent.System(ActivityEventKind.IdleEnded, start + 2_400_000),
            ActivityEvent.ExcludedForeground(start + 2_400_000),
            ActivityEvent.Foreground(start + 3_000_000, @"C:\code.exe", "code", "Ещё работа"),
            ActivityEvent.System(ActivityEventKind.TrackingPaused, start + 3_600_000),
        ]);
        var day = await new DayActivityService(_events, _materializer).ReadAsync(date);
        Assert.Empty(new SessionRepository(_factory).GetRange(start, end, _materializer.CurrentRules()));
        Assert.Equal(TimeSpan.FromMinutes(40), day.Report.Active);
        Assert.Equal(TimeSpan.FromMinutes(10), day.Report.Idle);
        Assert.Contains(day.Rows, r => r.IsExcluded);
        Assert.Equal(start, day.Rows[0].StartedUtcMs);
        var totals = Assert.Single(new StatsService(_materializer).GetDailyTotals(date, date.AddDays(1)));
        Assert.Equal(totals.Active, day.Report.Active);
        var path = Path.Combine(_directory, "day.json");
        var result = new ExportService(_materializer).WriteRange(path, start, end);
        Assert.Equal(OperationStatus.Success, result.Status);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(day.Rows.Count, json.RootElement.GetProperty("sessions").GetArrayLength());
        Assert.Equal(OperationStatus.Error, new ExportService(_materializer).WriteRange(_directory, start, end).Status);
        Assert.All(day.Rows, r => Assert.InRange(r.EndedUtcMs, start, end));
    }

    [Fact]
    public void Journal_SearchIsUnicodeAwareAndPaginationIsStable()
    {
        var (start, end) = LocalDay.Bounds(DateTime.Today.AddDays(-1));
        _events.AppendBatch(Enumerable.Range(0, 230).Select(i => ActivityEvent.Journal(ActivityEventKind.FileModified,
            start + 1000, "{\"path\":\"C:/ПРОЕКТ/файл" + i + ".cs\"}")).ToList());
        _events.AppendBatch([ActivityEvent.Journal(ActivityEventKind.ProcessStarted, start + 2000,
            "{\"exe\":\"dotnet.exe\",\"pid\":1}")]);
        var reader = new JournalReader(_factory);
        var first = reader.Read(start, end, 2, 14, "проект", 100);
        var next = reader.Read(start, end, 2, 14, "проект", 200);
        Assert.Equal(230, first.Total);
        Assert.Equal(100, first.Entries.Count);
        Assert.Equal(200, next.Entries.Count);
        Assert.Equal(first.Entries.Select(e => e.Id), next.Entries.Take(100).Select(e => e.Id));
        Assert.Equal(230, reader.Read(start, end, 0, 0, "ИЗМЕНЁН", 100).Total);
        Assert.Empty(reader.Read(start, end, 1, 14, "", 100).Entries);
        Assert.Empty(reader.Read(start, end, 2, 0, "%", 100).Entries);
    }

    [Fact]
    public async Task SlowOldDayCannotReplaceNewDayEvenWhenProviderIgnoresCancellation()
    {
        var provider = new ControlledDays();
        var context = new DayContext { SelectedDate = DateTime.Today.AddDays(-1) };
        var assistant = new ReportViewModel(_settings, context);
        var vm = new TodayViewModel(provider, context, assistant);
        var oldDate = context.SelectedDate;
        var oldLoad = vm.LoadAsync();
        context.GoTodayCommand.Execute(null);
        var newLoad = vm.LoadAsync();
        provider.Pending[1].SetResult(EmptyDay(context.SelectedDate, TimeSpan.FromHours(2)));
        await newLoad;
        Assert.Equal(TimeText.Humanize(TimeSpan.FromHours(2)), vm.ActiveLabel);
        provider.Pending[0].SetResult(EmptyDay(oldDate, TimeSpan.FromHours(1)));
        await oldLoad;
        Assert.Equal(TimeText.Humanize(TimeSpan.FromHours(2)), vm.ActiveLabel);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void Operations_DistinguishNoChangesAndFailureAndKeepSavedSettings()
    {
        var retention = new RetentionService(_settings, _events);
        Assert.Equal(OperationStatus.NoChanges, retention.ApplyRetention().Status);
        Assert.Equal(OperationStatus.NoChanges, new ExportService(_materializer).WriteRange(Path.Combine(_directory,"empty.csv"), 0, 1).Status);
        var s = _settings.Settings;
        s.RetentionDays = 90;
        Assert.True(_settings.TrySave(s).IsSuccess);
        var bad = _settings.Settings;
        bad.RetentionDays = -1;
        Assert.Equal(OperationStatus.Error, _settings.TrySave(bad).Status);
        Assert.Equal(90, _settings.Settings.RetentionDays);
        // A real filesystem failure, not a mocked success/error string.
        var brokenFactory = new SqliteConnectionFactory(Path.Combine(_directory, "missing", "db"));
        var broken = new RetentionService(_settings, new EventRepository(brokenFactory));
        Assert.Equal(OperationStatus.Error, broken.ApplyRetention().Status);
    }

    [Fact]
    public void TrayHintSetting_RoundTripsWithoutChangingExistingSettings()
    {
        var s = _settings.Settings;
        s.HasSeenTrayHint = true;
        s.WatchedRoots = [@"C:\Work"];
        _settings.Save(s);
        var loaded = new SettingsStore(_directory).Load();
        Assert.True(loaded.HasSeenTrayHint);
        Assert.Equal(s.WatchedRoots, loaded.WatchedRoots);
    }

    [Fact]
    public async Task SettingsDraft_RejectsInvalidNumbersAndCanBeDiscarded()
    {
        var files = new FileActivityCollector(_settings);
        var tracking = new TrackingService(_events, _materializer, new ForegroundCollector(_settings),
            new IdleMonitor(_settings), new SessionMonitor(), new ProcessSnapshotCollector(_settings),
            new SystemMetricsCollector(), files, _settings);
        var vm = new SettingsViewModel(_settings, tracking, files, new BrowserIntegrationService(),
            new RetentionService(_settings, _events));
        vm.IdleThresholdMinutes = "не число";
        vm.RetentionDays = "-1";
        Assert.True(vm.IsDirty);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.GeneralError);
        Assert.Equal(300, _settings.Settings.IdleThresholdSeconds);
        Assert.Equal(180, _settings.Settings.RetentionDays);
        Assert.False(tracking.IsStarted);
        vm.ResetCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.Equal("5", vm.IdleThresholdMinutes);
        Assert.Equal("180", vm.RetentionDays);
    }

    [Fact]
    public async Task Assistant_DisabledAndMissingModelDoNotStartARequest()
    {
        var context = new DayContext();
        var vm = new ReportViewModel(_settings, context);
        vm.SetDay(EmptyDay(context.SelectedDate, TimeSpan.Zero));
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Contains("Включите Ollama", vm.Status);
        var s = _settings.Settings;
        s.OllamaEnabled = true;
        _settings.Save(s);
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Contains("Выберите модель", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void IntervalAtMidnight_UsesEndOfDayLabel()
    {
        var (start, end) = LocalDay.Bounds(DateTime.Today.AddDays(-1));
        Assert.Equal("00:00–24:00", TodayViewModel.Range(start, end));
    }

    private static DayActivity EmptyDay(DateTime date, TimeSpan active)
    {
        var (start, end) = LocalDay.Bounds(date);
        return new(date, DayReport.Empty(start, end) with { Active = active },
            new DayNarrative(active, TimeSpan.Zero, TimeSpan.Zero, 0, []), []);
    }
    private sealed class ControlledDays : IDayActivityService
    {
        public List<TaskCompletionSource<DayActivity>> Pending { get; } = [];
        public Task<DayActivity> ReadAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            var source = new TaskCompletionSource<DayActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(source);
            return source.Task;
        }
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
