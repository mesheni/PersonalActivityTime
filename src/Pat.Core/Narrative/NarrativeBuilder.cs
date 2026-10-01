using Pat.Core.Categorization;
using Pat.Core.Context;
using Pat.Core.Events;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;

namespace Pat.Core.Narrative;

/// <summary>
/// Реконструкция дня: сегменты активности склеиваются в отрезки работы с общим
/// контекстом, заголовок выводится из доминирующего проекта (IDE-заголовки) или
/// категории; внутри отрезка перечисляются приложения, домены браузера и файлы.
/// Чистая функция над DayReport — тестируется без Win32 и БД, работает без LLM.
/// </summary>
public sealed class NarrativeBuilder
{
    public DayNarrative Build(
        DayReport report,
        IReadOnlyList<BrowserFact> browserFacts,
        IReadOnlyList<FileFact> fileFacts,
        CategoryRuleSet rules,
        NarrativeOptions? options = null)
    {
        var opts = options ?? NarrativeOptions.Default;

        var active = report.Segments
            .Where(s => !s.IsIdle && !s.IsExcluded)
            .OrderBy(s => s.StartUtcMs)
            .ToList();

        var contextSwitches = 0;
        for (var i = 1; i < active.Count; i++)
        {
            if (!string.Equals(active[i].AppPath, active[i - 1].AppPath, StringComparison.OrdinalIgnoreCase))
                contextSwitches++;
        }

        var drafts = new List<List<ActivitySegment>>();
        foreach (var seg in active)
        {
            if (drafts.Count > 0 && seg.StartUtcMs - drafts[^1][^1].EndUtcMs <= opts.MergeGapMs)
                drafts[^1].Add(seg);
            else
                drafts.Add([seg]);
        }

        var blocks = drafts
            .Select(d => BuildBlock(d, browserFacts, fileFacts, rules))
            .ToList();

        return new DayNarrative(
            report.Active,
            report.Idle,
            blocks.Count > 0 ? blocks.Max(b => b.Duration) : TimeSpan.Zero,
            contextSwitches,
            blocks);
    }

    private static NarrativeBlock BuildBlock(
        List<ActivitySegment> segments,
        IReadOnlyList<BrowserFact> browserFacts,
        IReadOnlyList<FileFact> fileFacts,
        CategoryRuleSet rules)
    {
        var start = segments[0].StartUtcMs;
        var end = segments[^1].EndUtcMs;

        var projectTime = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var categoryTime = new Dictionary<string, (TimeSpan Total, string Color)>(StringComparer.OrdinalIgnoreCase);
        var appTime = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var total = TimeSpan.Zero;

        foreach (var seg in segments)
        {
            var duration = TimeSpan.FromMilliseconds(seg.EndUtcMs - seg.StartUtcMs);
            total += duration;

            var (category, color) = rules.Classify(seg.AppName, seg.AppPath, seg.WindowTitle);
            var existing = categoryTime.TryGetValue(category, out var c) ? c : (Total: TimeSpan.Zero, Color: color);
            categoryTime[category] = (existing.Total + duration, existing.Color);

            appTime[seg.AppName] = appTime.TryGetValue(seg.AppName, out var a) ? a + duration : duration;

            if (IdeTitleExtractor.Extract(seg.WindowTitle) is { } ctx)
                projectTime[ctx.Project] = projectTime.TryGetValue(ctx.Project, out var p) ? p + duration : duration;
        }

        var dominant = categoryTime.OrderByDescending(kv => kv.Value.Total).First();

        // Проект — заголовок отрезка, если IDE-заголовки покрывают заметную часть времени
        var topProject = projectTime.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var projectDominant = topProject.Key is not null
            && topProject.Value >= TimeSpan.FromMinutes(5)
            && topProject.Value >= total * 0.25;

        var details = new List<string>();
        foreach (var (category, value) in categoryTime.OrderByDescending(kv => kv.Value.Total).Take(3))
            details.Add($"{category} — {TimeText.Humanize(value.Total)}");
        if (projectDominant)
        {
            foreach (var (app, time) in appTime.OrderByDescending(kv => kv.Value).Take(2))
                details.Add($"{app} — {TimeText.Humanize(time)}");
        }

        var blockBrowser = browserFacts
            .Where(f => f.TsUtcMs >= start && f.TsUtcMs < end)
            .GroupBy(f => f.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .ToList();
        foreach (var group in blockBrowser)
            details.Add(group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key);

        var (created, modified) = CountFiles(fileFacts, start, end);
        if (created + modified > 0)
            details.Add($"файлы: создано {created}, изменено {modified}");

        var title = projectDominant
            ? $"Работа над {topProject.Key}"
            : dominant.Key;

        return new NarrativeBlock(start, end, title, dominant.Key, dominant.Value.Color, details);
    }

    private static (long Created, long Modified) CountFiles(IReadOnlyList<FileFact> fileFacts, long start, long end)
    {
        long created = 0, modified = 0;
        foreach (var f in fileFacts)
        {
            if (f.TsUtcMs < start || f.TsUtcMs >= end)
                continue;
            if (f.Kind == ActivityEventKind.FileCreated) created++;
            else if (f.Kind == ActivityEventKind.FileModified) modified++;
        }
        return (created, modified);
    }
}
