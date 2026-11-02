using Pat.Core;
using Pat.Core.Narrative;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;

namespace Pat.Infrastructure.Database;

public sealed record DayActivity(DateTime Date, DayReport Report, DayNarrative Narrative,
    IReadOnlyList<SessionRow> Rows);

public interface IDayActivityService
{
    Task<DayActivity> ReadAsync(DateTime date, CancellationToken cancellationToken = default);
}

/// <summary>Read-only reconstruction, independent of the materialized sessions cache.</summary>
public sealed class DayActivityService(EventRepository events, SessionMaterializer materializer,
    TimeProvider? clock = null) : IDayActivityService
{
    public Task<DayActivity> ReadAsync(DateTime date, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (start, end) = LocalDay.Bounds(date);
            end = Math.Min(end, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
            var rows = materializer.BuildRows(start, end);
            cancellationToken.ThrowIfCancellationRequested();
            var rules = materializer.CurrentRules();
            var segments = rows.Select(r => new ActivitySegment(r.StartedUtcMs, r.EndedUtcMs,
                r.AppPath, r.AppName ?? "(исключено)", r.WindowTitle, r.IsIdle, r.IsExcluded)).ToList();
            var report = new DayReportBuilder().Build(segments, start, Math.Max(start, end), rules);
            var browser = events.GetBrowserVisits(start, end, int.MaxValue)
                .Select(v => new BrowserFact(v.TsUtcMs, v.Domain)).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            var files = events.GetFileEvents(start, end, int.MaxValue)
                .Select(f => new FileFact(f.TsUtcMs, f.Kind, f.Path)).ToList();
            var narrative = new NarrativeBuilder().Build(report, browser, files, rules);
            cancellationToken.ThrowIfCancellationRequested();
            return new DayActivity(date.Date, report, narrative, rows);
        }, cancellationToken);
}
