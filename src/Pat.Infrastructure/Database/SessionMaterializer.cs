using Pat.Core.Categorization;
using Pat.Core.Context;
using Pat.Core.Sessionization;
using Pat.Infrastructure.Settings;

namespace Pat.Infrastructure.Database;

/// <summary>
/// Пересборка сессий из журнала raw_events за текущий локальный день.
/// Сырой журнал — источник истины: при смене правил категорий достаточно
/// вызвать Refresh снова, вся история дней пересчитывается тем же кодом.
/// </summary>
public sealed class SessionMaterializer(
    EventRepository events,
    SessionRepository sessions,
    SettingsStore settingsStore)
{
    private readonly object _gate = new();
    private readonly SessionizationEngine _engine = new();

    public CategoryRuleSet CurrentRules()
    {
        var settings = settingsStore.Settings;
        return settings.CategoryRules is { } rules
            ? new CategoryRuleSet(rules)
            : CategoryRuleSet.CreateDefault();
    }

    public IReadOnlyList<ActivitySegment> BuildRange(long fromUtcMs, long toUtcMs)
    {
        if (toUtcMs <= fromUtcMs) return [];
        var journal = events.GetReplayRange(fromUtcMs, toUtcMs + 1);
        return _engine.Build(journal, toUtcMs)
            .Where(s => s.EndUtcMs > fromUtcMs && s.StartUtcMs < toUtcMs)
            .Select(s => s.ClipTo(fromUtcMs, toUtcMs))
            .Where(s => s.EndUtcMs > s.StartUtcMs).ToList();
    }

    public List<SessionRow> BuildRows(long fromUtcMs, long toUtcMs)
    {
        var rules = CurrentRules();

        return BuildRange(fromUtcMs, toUtcMs).Select(seg =>
        {
            var (category, color) = seg.IsIdle
                ? ("Idle", "#4B5563")
                : seg.IsExcluded
                    ? ("(исключено)", "#374151")
                    : rules.Classify(seg.AppName, seg.AppPath, seg.WindowTitle);
            var project = IdeTitleExtractor.Extract(seg.WindowTitle)?.Project;
            return new SessionRow(
                seg.StartUtcMs, seg.EndUtcMs, seg.AppPath, seg.AppName, seg.WindowTitle,
                project, category, color, seg.IsIdle, seg.IsExcluded);
        }).ToList();
    }

    public void Refresh(long fromUtcMs, long nowUtcMs)
    {
        lock (_gate) sessions.ReplaceFrom(fromUtcMs, BuildRows(fromUtcMs, nowUtcMs));
    }
}
