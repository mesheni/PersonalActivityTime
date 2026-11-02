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
/// материализации тоже видны. Состояние восстанавливается от границы запуска,
/// одним чтением для всего периода; открытый день ограничен текущим временем.
/// </summary>
public sealed class StatsService(SessionMaterializer materializer, TimeProvider? clock = null)
{
    private readonly DayReportBuilder _reportBuilder = new();

    public List<DayTotals> GetDailyTotals(DateTime fromDate, DateTime toDateExclusive, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var rules = materializer.CurrentRules();
        var result = new List<DayTotals>();
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var (rangeStart, _) = LocalDay.Bounds(fromDate, tz);
        var (_, rangeEnd) = LocalDay.Bounds(toDateExclusive.AddDays(-1), tz);
        var segments = materializer.BuildRange(rangeStart, Math.Min(rangeEnd, now));

        for (var date = fromDate.Date; date < toDateExclusive.Date; date = date.AddDays(1))
        {
            var (start, end) = LocalDay.Bounds(date, tz);
            if (start >= now) break;
            var report = _reportBuilder.Build(segments, start, Math.Min(end, now), rules);

            result.Add(new DayTotals(date, report.Active, report.Idle, report.Categories));
        }

        return result;
    }
}
