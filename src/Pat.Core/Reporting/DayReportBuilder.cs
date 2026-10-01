using Pat.Core.Sessionization;

namespace Pat.Core.Reporting;

public sealed record CategoryTotal(string Name, string ColorHex, TimeSpan Total);

public sealed record AppTotal(
    string AppName,
    string? ExePath,
    string CategoryName,
    string CategoryColorHex,
    TimeSpan Total);

/// <summary>Отчёт по одному локальному дню: уже обрезан по границам суток.</summary>
public sealed record DayReport(
    long DayStartUtcMs,
    long DayEndUtcMs,
    TimeSpan Active,
    TimeSpan Idle,
    IReadOnlyList<CategoryTotal> Categories,
    IReadOnlyList<AppTotal> Apps,
    IReadOnlyList<ActivitySegment> Segments)
{
    public static DayReport Empty(long dayStartUtcMs, long dayEndUtcMs) =>
        new(dayStartUtcMs, dayEndUtcMs, TimeSpan.Zero, TimeSpan.Zero, [], [], []);
}

public sealed class DayReportBuilder
{
    /// <param name="segments">Сегменты (могут выходить за границы дня — обрежутся).</param>
    /// <param name="classifier">Правила категоризации (применяются на этапе отчёта, не сбора).</param>
    public DayReport Build(
        IReadOnlyList<ActivitySegment> segments,
        long dayStartUtcMs,
        long dayEndUtcMs,
        Categorization.CategoryRuleSet classifier)
    {
        var clipped = segments
            .Where(s => s.EndUtcMs > dayStartUtcMs && s.StartUtcMs < dayEndUtcMs)
            .Select(s => s.ClipTo(dayStartUtcMs, dayEndUtcMs))
            .ToList();

        var active = TimeSpan.Zero;
        var idle = TimeSpan.Zero;
        var byCategory = new Dictionary<string, (TimeSpan Total, string Color)>();
        var byApp = new Dictionary<string, AppTotal>(StringComparer.OrdinalIgnoreCase);

        foreach (var seg in clipped)
        {
            var duration = TimeSpan.FromMilliseconds(seg.EndUtcMs - seg.StartUtcMs);
            if (seg.IsIdle)
            {
                idle += duration;
                continue;
            }

            if (seg.IsExcluded)
                continue; // в отчёт не входит, видно только на таймлайне

            active += duration;
            var (category, color) = classifier.Classify(seg.AppName, seg.AppPath, seg.WindowTitle);
            byCategory[category] = (
                byCategory.TryGetValue(category, out var c) ? c.Total + duration : duration,
                color);

            var appKey = seg.AppPath ?? seg.AppName;
            if (byApp.TryGetValue(appKey, out var existing))
                byApp[appKey] = existing with { Total = existing.Total + duration };
            else
                byApp[appKey] = new AppTotal(seg.AppName, seg.AppPath, category, color, duration);
        }

        var categories = byCategory
            .Select(kv => new CategoryTotal(kv.Key, kv.Value.Color, kv.Value.Total))
            .OrderByDescending(c => c.Total)
            .ToList();

        var apps = byApp.Values.OrderByDescending(a => a.Total).ToList();

        return new DayReport(dayStartUtcMs, dayEndUtcMs, active, idle, categories, apps, clipped);
    }
}
