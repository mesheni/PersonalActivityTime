using Pat.Core.Categorization;
using Pat.Core.Events;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;

namespace Pat.Core.Tests;

/// <summary>
/// Интеграционный прогон реального конвейера на временной SQLite-базе:
/// миграции → запись журнала → материализация сессий → чтение с категориями.
/// </summary>
public sealed class DatabasePipelineTests : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly EventRepository _events;
    private readonly SessionRepository _sessions;
    private readonly SessionMaterializer _materializer;
    private readonly SettingsStore _settings = new();

    public DatabasePipelineTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pat-test-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(dbPath);
        new MigrationRunner(_factory).Run();
        _events = new EventRepository(_factory);
        _sessions = new SessionRepository(_factory);
        _materializer = new SessionMaterializer(_events, _sessions, _settings);
    }

    public void Dispose()
    {
        try { File.Delete(_factory.DatabasePath); } catch { /* временная база, не важно */ }
    }

    [Fact]
    public void FullPipeline_JournalToSessionsWithCategories()
    {
        var t0 = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddHours(10)).ToUnixTimeMilliseconds();
        long Min(int m) => t0 + m * 60_000L;

        _events.AppendBatch(
        [
            ActivityEvent.Foreground(Min(0), @"C:\Rider\bin\rider64.exe", "rider64", "Parser.cs — Wafer — JetBrains Rider"),
            ActivityEvent.Foreground(Min(30), @"C:\Chrome\chrome.exe", "chrome", "GitHub"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, Min(40)),
            ActivityEvent.System(ActivityEventKind.IdleEnded, Min(50)),
            ActivityEvent.Foreground(Min(50), @"C:\Chrome\chrome.exe", "chrome", "docs"),
        ]);

        var (dayStart, dayEnd) = Pat.Core.LocalDay.Today();
        _materializer.Refresh(Math.Min(dayStart, t0), Min(60));

        var rules = _materializer.CurrentRules();
        var rows = _sessions.GetRange(Math.Min(dayStart, t0), Min(60), rules);

        Assert.Contains(rows, r => r.AppName == "rider64" && r.CategoryName == "Программирование");
        Assert.Contains(rows, r => r.AppName == "chrome" && r.CategoryName == "Браузер");
        Assert.Contains(rows, r => r.IsIdle);
        Assert.Contains(rows, r => r.Project == "Wafer"); // extracted from IDE title

        // Rider сегмент 0–30 минут
        var rider = rows.Single(r => r.AppName == "rider64");
        Assert.Equal((Min(0), Min(30)), (rider.StartedUtcMs, rider.EndedUtcMs));
    }

    [Fact]
    public void MaterializationIsIdempotent_NoDuplicatesOnRerun()
    {
        var t0 = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddHours(9)).ToUnixTimeMilliseconds();

        _events.AppendBatch(
        [
            ActivityEvent.Foreground(t0, @"C:\Rider\bin\rider64.exe", "rider64", "Wafer.sln"),
        ]);

        var from = t0 - 60_000;
        _materializer.Refresh(from, t0 + 3_600_000);
        _materializer.Refresh(from, t0 + 3_600_000);

        var rows = _sessions.GetRange(from, t0 + 3_600_000, CategoryRuleSet.CreateDefault());
        Assert.Single(rows);
    }

    [Fact]
    public void ExcludedApp_StoredWithoutPathOrTitle()
    {
        var t0 = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddHours(11)).ToUnixTimeMilliseconds();

        _events.AppendBatch(
        [
            ActivityEvent.Foreground(t0, @"C:\Rider\bin\rider64.exe", "rider64", "code"),
            ActivityEvent.ExcludedForeground(t0 + 60_000),
            ActivityEvent.Foreground(t0 + 120_000, @"C:\Rider\bin\rider64.exe", "rider64", "code"),
        ]);

        _materializer.Refresh(t0 - 60_000, t0 + 300_000);

        var rows = _sessions.GetRange(t0 - 60_000, t0 + 300_000, CategoryRuleSet.CreateDefault());
        var excluded = rows.Single(r => r.IsExcluded);
        Assert.Null(excluded.AppPath);
        Assert.Null(excluded.WindowTitle);
        Assert.Equal("(исключено)", excluded.CategoryName);
    }

    [Fact]
    public void ProcessEvents_PayloadRoundTripAndQueries()
    {
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _events.AppendBatch(
        [
            ActivityEvent.Journal(ActivityEventKind.ProcessStarted, t0,
                """{"pid":123,"ppid":10,"exe":"dotnet.exe","parent":"rider64.exe"}"""),
            ActivityEvent.Journal(ActivityEventKind.ProcessStopped, t0 + 1000,
                """{"pid":123,"ppid":10,"exe":"dotnet.exe","parent":null}"""),
            ActivityEvent.Journal(ActivityEventKind.SystemSample, t0 + 2000,
                """{"cpu":12.5,"ramPct":55.1,"ramUsedGb":8.8,"ramTotalGb":16.0}"""),
        ]);

        var processEvents = _events.GetProcessEvents(t0 - 1000, t0 + 10_000, 10);
        Assert.Equal(2, processEvents.Count);
        // новые сверху
        Assert.False(processEvents[0].Started);
        Assert.True(processEvents[1].Started);
        Assert.Equal("dotnet.exe", processEvents[1].ExeName);
        Assert.Equal(123u, processEvents[1].Pid);
        Assert.Equal("rider64.exe", processEvents[1].ParentName);

        Assert.Equal(1, _events.CountProcessStarts(t0 - 1000, t0 + 10_000));

        var samples = _events.GetMetricSamples(t0 - 1000, t0 + 10_000);
        var sample = Assert.Single(samples);
        Assert.Equal(12.5, sample.CpuPercent);
        Assert.Equal(16.0, sample.RamTotalGb);
    }

    [Fact]
    public void Lookback_ForegroundBeforeMidnightCountedAfterMidnight()
    {
        // Работа началась в 23:50 вчера и продолжается после полуночи:
        // сегмент должен начаться ровно в полночь, а не потеряться
        var (yStart, midnight) = Pat.Core.LocalDay.Bounds(DateTime.Now.Date.AddDays(-1));
        var beforeMidnight = midnight - 10 * 60_000; // 23:50 вчера, в прошлом
        _events.AppendBatch(
        [
            ActivityEvent.Foreground(beforeMidnight, @"C:\Rider\bin\rider64.exe", "rider64", "Wafer"),
        ]);

        // Материализуем сегодняшний день: lookback подхватит вчерашнее FG-событие
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _materializer.Refresh(midnight, now);

        var rows = _sessions.GetRange(midnight, now + 1, CategoryRuleSet.CreateDefault());
        var rider = rows.FirstOrDefault(r => r.AppName == "rider64");
        Assert.NotNull(rider);
        Assert.Equal(midnight, rider.StartedUtcMs); // клип по границе дня
    }

    [Fact]
    public void StatsService_MultiDayTotalsFromJournal()
    {
        var today = DateTime.Now.Date;
        var day1Start = new DateTimeOffset(today.AddDays(-1).AddHours(12)).ToUnixTimeMilliseconds();
        var day2Start = new DateTimeOffset(today.AddHours(10)).ToUnixTimeMilliseconds();

        // Вчера: 30 мин rider + 10 мин explorer; сегодня: 20 мин chrome.
        // Терминаторы (IdleBegan) фиксируют концы, чтобы результат не зависел от текущего времени.
        _events.AppendBatch(
        [
            ActivityEvent.Foreground(day1Start, @"C:\Rider\bin\rider64.exe", "rider64", "Wafer"),
            ActivityEvent.Foreground(day1Start + 30 * 60_000, @"C:\explorer.exe", "explorer", "files"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, day1Start + 40 * 60_000),
            ActivityEvent.System(ActivityEventKind.IdleEnded, day2Start),
            ActivityEvent.Foreground(day2Start, @"C:\chrome.exe", "chrome", "docs"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, day2Start + 20 * 60_000),
        ]);

        var stats = new StatsService(_materializer);
        var totals = stats.GetDailyTotals(today.AddDays(-1), today.AddDays(1));

        Assert.Equal(2, totals.Count);
        Assert.Equal(today.AddDays(-1), totals[0].LocalDate);
        Assert.Equal(TimeSpan.FromMinutes(40), totals[0].Active);
        Assert.Contains(totals[0].Categories, c => c.Name == "Программирование" && c.Total == TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(20), totals[1].Active);
        Assert.Contains(totals[1].Categories, c => c.Name == "Браузер" && c.Total == TimeSpan.FromMinutes(20));
    }
}
