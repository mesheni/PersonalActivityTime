using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Pat.Core.Export;

/// <summary>Одна строка экспорта: интервал активности с приложением и категорией.</summary>
public sealed record ExportSession(
    long StartedUtcMs,
    long EndedUtcMs,
    string AppName,
    string? WindowTitle,
    string? Project,
    string Category,
    bool IsIdle,
    bool IsExcluded);

/// <summary>
/// Чистый экспорт сессий в CSV и JSON. CSV — RFC 4180 с разделителем «;»
/// (Excel в русской локали сразу раскрывает по колонкам), заголовки в UTF-8 с BOM
/// добавляет записывающая сторона. Времена — локальные, формат «yyyy-MM-dd HH:mm:ss».
/// </summary>
public static class ActivityExporter
{
    public static string ToCsv(IReadOnlyList<ExportSession> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("start;end;minutes;category;app;project;window_title;idle;excluded");
        foreach (var s in sessions)
        {
            sb.Append(CsvField(ToLocalIso(s.StartedUtcMs))).Append(';');
            sb.Append(CsvField(ToLocalIso(s.EndedUtcMs))).Append(';');
            sb.Append(Minutes(s).ToString("0.0", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(CsvField(s.Category)).Append(';');
            sb.Append(CsvField(s.AppName)).Append(';');
            sb.Append(CsvField(s.Project ?? "")).Append(';');
            sb.Append(CsvField(s.WindowTitle ?? "")).Append(';');
            sb.Append(s.IsIdle ? '1' : '0').Append(';');
            sb.Append(s.IsExcluded ? '1' : '0');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private sealed record JsonSession(
        string Start, string End, double Minutes, string Category, string App,
        string? Project, string? Title, bool Idle, bool Excluded);

    private sealed record JsonExport(
        string ExportedAt, int SessionCount, double TotalMinutes, List<JsonSession> Sessions);

    public static string ToJson(IReadOnlyList<ExportSession> sessions)
    {
        var document = new JsonExport(
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            sessions.Count,
            Math.Round(sessions.Sum(Minutes), 1),
            [.. sessions.Select(s => new JsonSession(
                ToLocalIso(s.StartedUtcMs),
                ToLocalIso(s.EndedUtcMs),
                Math.Round(Minutes(s), 1),
                s.Category,
                s.AppName,
                string.IsNullOrWhiteSpace(s.Project) ? null : s.Project,
                string.IsNullOrWhiteSpace(s.WindowTitle) ? null : s.WindowTitle,
                s.IsIdle,
                s.IsExcluded))]);

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static double Minutes(ExportSession s) => (s.EndedUtcMs - s.StartedUtcMs) / 60000.0;

    private static string ToLocalIso(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string CsvField(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
