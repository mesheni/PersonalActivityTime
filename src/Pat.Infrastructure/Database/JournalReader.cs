using Dapper;

namespace Pat.Infrastructure.Database;

public sealed record JournalEntry(long Id, long TsUtcMs, long Kind, string Action, string Name, string Details)
{
    // SQLite reports expression columns as BLOB for an empty result. Property mapping avoids
    // Dapper trying to resolve a positional constructor from those placeholder types.
    public JournalEntry() : this(0, 0, 0, "", "", "") { }
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(TsUtcMs).ToLocalTime().ToString("HH:mm:ss");
    public string FullDetails => $"{Time} · {Action}\n{Name}\n{Details}";
}
public sealed record JournalPage(IReadOnlyList<JournalEntry> Entries, long Total);

public sealed class JournalReader(SqliteConnectionFactory factory)
{
    // Search exactly the displayed strings, with Unicode-aware casing and literal wildcard characters.
    private const string Rows = """
        WITH entries AS (
          SELECT id, ts_utc TsUtcMs, kind,
            CASE kind WHEN 10 THEN 'Запущен' WHEN 11 THEN 'Завершён'
              WHEN 13 THEN 'Создан' WHEN 14 THEN 'Изменён' WHEN 15 THEN 'Удалён'
              WHEN 16 THEN 'Переименован' WHEN 17 THEN 'Визит' END Action,
            CASE WHEN kind IN (10,11) THEN COALESCE(json_extract(payload,'$.exe'),'?')
              WHEN kind BETWEEN 13 AND 16 THEN COALESCE(json_extract(payload,'$.path'),'?')
              WHEN kind = 17 THEN COALESCE(json_extract(payload,'$.domain'),'?') END Name,
            CASE WHEN kind IN (10,11) THEN 'PID ' || COALESCE(json_extract(payload,'$.pid'),'?') || ' · ' || COALESCE(json_extract(payload,'$.parent'),'')
              WHEN kind BETWEEN 13 AND 16 THEN COALESCE(json_extract(payload,'$.oldPath'),'')
              WHEN kind = 17 THEN COALESCE(json_extract(payload,'$.title'),'') || char(10) || COALESCE(json_extract(payload,'$.url'),'') || char(10) || COALESCE(json_extract(payload,'$.browser'),'') END Details
          FROM raw_events
          WHERE ts_utc >= @start AND ts_utc < @end AND kind IN (10,11,13,14,15,16,17)
            AND (@group = 0 OR (@group = 1 AND kind IN (10,11))
              OR (@group = 2 AND kind BETWEEN 13 AND 16) OR (@group = 3 AND kind = 17))
            AND (@eventKind = 0 OR kind = @eventKind)
        )
        """;
    private const string Filter = " WHERE contains_text(Action || ' ' || Name || ' ' || Details || ' ' || strftime('%H:%M:%S', TsUtcMs / 1000, 'unixepoch', 'localtime'), @search)";

    public JournalPage Read(long start, long end, int group, int eventKind, string search, int limit)
    {
        using var connection = factory.Open();
        connection.CreateFunction<string?, string?, bool>("contains_text", (text, query) =>
            string.IsNullOrWhiteSpace(query) || (text?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ?? false), isDeterministic: true);
        var args = new { start, end, group, eventKind, search, limit = Math.Max(limit, 100) };
        using var transaction = connection.BeginTransaction();
        var total = connection.ExecuteScalar<long>(Rows + "SELECT COUNT(*) FROM entries" + Filter, args, transaction);
        var entries = connection.Query<JournalEntry>(Rows + "SELECT * FROM entries" + Filter +
            " ORDER BY TsUtcMs DESC, id DESC LIMIT @limit", args, transaction).ToList();
        transaction.Commit();
        return new(entries, total);
    }
}
