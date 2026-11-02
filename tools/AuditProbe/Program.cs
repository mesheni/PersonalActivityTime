using Dapper;
using Microsoft.Data.Sqlite;
using Pat.Core;
using Pat.Core.Categorization;
using Pat.Core.Events;
using Pat.Core.Sessionization;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;

// Audit diagnostics: prints observed defects, not a replacement for regression tests.
// Uses synthetic data and a new temporary directory; never loads user settings/data.
var root = Path.Combine(Path.GetTempPath(), "pat-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Temporary databases: " + root);
var engine = new SessionizationEngine();
ActivityEvent F(long ts, string title = "code") => ActivityEvent.Foreground(ts, @"C:\audit\editor.exe", "editor", title);
ActivityEvent S(ActivityEventKind kind, long ts) => ActivityEvent.System(kind, ts);
void Show(string id, object actual, object expected) => Console.WriteLine($"{id}: observed={actual}; expected={expected}");
(SqliteConnectionFactory Factory, EventRepository Events, SessionRepository Sessions, SessionMaterializer Materializer) Db(string name)
{
    var factory = new SqliteConnectionFactory(Path.Combine(root, name + ".db"));
    new MigrationRunner(factory).Run();
    var events = new EventRepository(factory);
    var sessions = new SessionRepository(factory);
    return (factory, events, sessions, new SessionMaterializer(events, sessions, new SettingsStore()));
}

var blocked = engine.Build([F(0), S(ActivityEventKind.SessionLocked, 60_000), F(120_000, "auto title")], 600_000);
Show("A01 active milliseconds after lock", blocked.Where(x => !x.IsIdle).Sum(x => x.EndUtcMs - x.StartUtcMs), 60_000);
var idle = engine.Build([F(0), S(ActivityEventKind.IdleBegan, 60_000), F(120_000, "auto title")], 600_000);
Show("A02 active milliseconds with no IdleEnded", idle.Where(x => !x.IsIdle).Sum(x => x.EndUtcMs - x.StartUtcMs), 60_000);

var today = DateTime.Now.Date;
var (dayStart, dayEnd) = LocalDay.Bounds(today);
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var statsDb = Db("stats");
statsDb.Events.AppendBatch([F(dayStart)]);
var totals = new StatsService(statsDb.Events, statsDb.Materializer).GetDailyTotals(today, today.AddDays(1)).Single();
Show("A03 today's active milliseconds", totals.Active.TotalMilliseconds, "<= " + (now - dayStart + 1000));

var midnightDb = Db("midnight");
midnightDb.Events.AppendBatch([F(dayStart - 60 * 60_000), S(ActivityEventKind.TrackingPaused, dayStart - 30 * 60_000)]);
midnightDb.Materializer.Refresh(dayStart, dayStart + 60_000);
using (var connection = midnightDb.Factory.Open())
    Show("A04 sessions with end <= start", connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sessions WHERE ended_utc <= started_utc"), 0);

var lookbackDb = Db("lookback");
lookbackDb.Events.AppendBatch([F(dayStart - 3 * 60 * 60_000)]);
lookbackDb.Materializer.Refresh(dayStart, dayStart + 60_000);
Show("A05 ongoing foreground from 21:00: rows after midnight", lookbackDb.Sessions.GetRange(dayStart, dayStart + 60_000, CategoryRuleSet.CreateDefault()).Count, 1);

var exportDb = Db("export");
var (yesterdayStart, _) = LocalDay.Bounds(today.AddDays(-1));
exportDb.Events.AppendBatch([F(yesterdayStart + 60 * 60_000), S(ActivityEventKind.TrackingPaused, yesterdayStart + 120 * 60_000)]);
var yesterdayStats = new StatsService(exportDb.Events, exportDb.Materializer).GetDailyTotals(today.AddDays(-1), today).Single();
Show("A06 historical stats minutes / export-source rows", $"{yesterdayStats.Active.TotalMinutes} / {exportDb.Sessions.GetRange(yesterdayStart, dayStart, CategoryRuleSet.CreateDefault()).Count}", "60 / 1");

var rollbackDb = Db("rollback");
using (var connection = rollbackDb.Factory.Open())
    connection.Execute("CREATE TRIGGER audit_fail BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT, 'audit injected failure'); END;");
try { rollbackDb.Events.AppendBatch([F(dayStart)]); }
catch (SqliteException) { }
using (var connection = rollbackDb.Factory.Open())
    connection.Execute("DROP TRIGGER audit_fail;");
try
{
    rollbackDb.Events.AppendBatch([F(dayStart + 1)]);
    Show("A07 append after transaction rollback", "success", "success");
}
catch (SqliteException ex)
{
    Show("A07 append after transaction rollback", $"SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}: {ex.Message}", "success");
}

var exitGap = engine.Build([F(0), F(600_000, "new run")], 660_000);
Show("A08 active ms across unrecorded shutdown at 60s and restart at 600s", exitGap.Sum(x => x.EndUtcMs - x.StartUtcMs), 120_000);

var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
foreach (var (date, expectedHours) in new[] { (new DateTime(2026, 3, 8), 23), (new DateTime(2026, 11, 1), 25) })
{
    var (start, end) = LocalDay.Bounds(date, eastern);
    Show($"A09 local day hours {date:yyyy-MM-dd} in Eastern", (end - start) / 3_600_000.0, expectedHours);
}

// Leave only generated audit databases in the reported temp directory for inspection.
SqliteConnection.ClearAllPools();
