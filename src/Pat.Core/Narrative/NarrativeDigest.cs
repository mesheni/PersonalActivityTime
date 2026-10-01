using System.Text;
using Pat.Core.Reporting;

namespace Pat.Core.Narrative;

/// <summary>
/// Компактный текстовый дайджест дня для локальной LLM: агрегированные факты
/// (категории, приложения, отрезки), не сырой журнал событий. Ограничен по размеру.
/// </summary>
public static class NarrativeDigest
{
    public static string Build(DayReport report, DayNarrative narrative, int maxBlocks = 15)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine($"Активное время: {TimeText.Humanize(report.Active)}; простой: {TimeText.Humanize(report.Idle)}.");
        if (narrative.Blocks.Count > 0)
            sb.AppendLine($"Отрезков работы: {narrative.Blocks.Count}; самый длинный: {TimeText.Humanize(narrative.LongestStreak)}; смен контекста: {narrative.ContextSwitches}.");

        if (report.Categories.Count > 0)
        {
            sb.AppendLine("Категории:");
            foreach (var c in report.Categories.Take(8))
                sb.AppendLine($"- {c.Name}: {TimeText.Humanize(c.Total)}");
        }

        if (report.Apps.Count > 0)
        {
            sb.AppendLine("Приложения:");
            foreach (var a in report.Apps.Take(8))
                sb.AppendLine($"- {a.AppName} ({a.CategoryName}): {TimeText.Humanize(a.Total)}");
        }

        if (narrative.Blocks.Count > 0)
        {
            sb.AppendLine("Хроника (местное время):");
            var shown = 0;
            foreach (var block in narrative.Blocks)
            {
                if (shown >= maxBlocks)
                {
                    sb.AppendLine($"…и ещё {narrative.Blocks.Count - shown} более коротких отрезков.");
                    break;
                }
                var start = DateTimeOffset.FromUnixTimeMilliseconds(block.StartUtcMs).ToLocalTime();
                var end = DateTimeOffset.FromUnixTimeMilliseconds(block.EndUtcMs).ToLocalTime();
                var details = block.Details.Count > 0 ? $": {string.Join("; ", block.Details)}" : "";
                sb.AppendLine($"- {start:HH:mm}-{end:HH:mm} ({TimeText.Humanize(block.Duration)}) — {block.Title}{details}");
                shown++;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
