using Pat.Core;
using Pat.Core.Categorization;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;

namespace Pat.Infrastructure.Database;

public sealed record DayTotals(
    DateTime LocalDate,
    TimeSpan Active,
    TimeSpan Idle,
    IReadOnlyList<CategoryTotal> Categories);

/// <summary>
/// Итоги по дням для недели/месяца. Считаются из журнала raw_events (источник истины),
/// а не из кэша sessions: правила категорий применяются актуальные, дни без
/// материализации тоже видны. Для каждого дня используется тот же движок + lookback,
/// что и в SessionMaterializer — разрез полуночи консистентен.
/// </summary>
public sealed class StatsService(EventRepository events, SessionMaterializer materializer)
{
    private readonly SessionizationEngine _engine = new();
    private readonly DayReportBuilder _reportBuilder = new();

    public List<DayTotals> GetDailyTotals(DateTime fromDate, DateTime toDateExclusive, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var rules = materializer.CurrentRules();
        var result = new List<DayTotals>();

        for (var date = fromDate.Date; date < toDateExclusive.Date; date = date.AddDays(1))
        {
            var (start, end) = LocalDay.Bounds(date, tz);
            var journal = events.GetRange(start - SessionMaterializer.LookbackMs, end);
            var segments = _engine.Build(journal, end);
            var report = _reportBuilder.Build(segments, start, end, rules);

            result.Add(new DayTotals(date, report.Active, report.Idle, report.Categories));
        }

        return result;
    }
}
