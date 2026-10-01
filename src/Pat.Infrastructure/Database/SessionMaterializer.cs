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
    /// <summary>
    /// Журнал читается с запасом назад: событие IdleBegan/ForegroundChanged до полуночи
    /// нужно, чтобы активность после 00:00 не потерялась. 2 часа перекрывают любой
    /// настраиваемый порог простоя.
    /// </summary>
    public static readonly long LookbackMs = TimeSpan.FromHours(2).Ticks / TimeSpan.TicksPerMillisecond;

    private readonly SessionizationEngine _engine = new();

    public CategoryRuleSet CurrentRules()
    {
        var settings = settingsStore.Settings;
        return settings.CategoryRules is { Count: > 0 } rules
            ? new CategoryRuleSet(rules)
            : CategoryRuleSet.CreateDefault();
    }

    public void Refresh(long fromUtcMs, long nowUtcMs)
    {
        var journal = events.GetRange(fromUtcMs - LookbackMs, nowUtcMs + 1);
        var segments = _engine.Build(journal, nowUtcMs);
        var rules = CurrentRules();

        var rows = segments.Select(seg =>
        {
            var (category, _) = seg.IsIdle
                ? ("Idle", "")
                : seg.IsExcluded
                    ? ("(исключено)", "")
                    : rules.Classify(seg.AppName, seg.AppPath, seg.WindowTitle);
            var project = IdeTitleExtractor.Extract(seg.WindowTitle)?.Project;
            var clipped = seg.ClipTo(fromUtcMs, long.MaxValue);
            return new SessionRow(
                clipped.StartUtcMs, clipped.EndUtcMs, seg.AppPath, seg.AppName, seg.WindowTitle,
                project, category, "", seg.IsIdle, seg.IsExcluded);
        }).ToList();

        sessions.ReplaceFrom(fromUtcMs, rows);
    }
}
