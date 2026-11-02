using Dapper;
using Microsoft.Data.Sqlite;
using Pat.Core.Events;

namespace Pat.Infrastructure.Database;

/// <summary>Строка сессии после материализации (категория уже присвоена).</summary>
public sealed record SessionRow(
    long StartedUtcMs,
    long EndedUtcMs,
    string? AppPath,
    string? AppName,
    string? WindowTitle,
    string? Project,
    string CategoryName,
    string CategoryColorHex,
    bool IsIdle,
    bool IsExcluded);

/// <summary>
/// Кэш соответствия exe-путь → id в applications в пределах одной транзакции.
/// Экземпляр нельзя переносить между транзакциями, включая откат.
/// </summary>
public sealed class AppRegistry
{
    private readonly Dictionary<string, long> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public long GetOrAdd(SqliteConnection connection, string exePath, string exeName, long nowUtcMs, SqliteTransaction? transaction = null)
    {
        if (_byPath.TryGetValue(exePath, out var id))
            return id;

        var inserted = connection.ExecuteScalar<long?>(
            """
            INSERT INTO applications (exe_path, exe_name, first_seen_utc)
            VALUES (@path, @name, @now)
            ON CONFLICT(exe_path) DO NOTHING
            RETURNING id
            """,
            new { path = exePath, name = exeName, now = nowUtcMs }, transaction);
        if (inserted is { } newId)
        {
            _byPath[exePath] = newId;
            return newId;
        }

        var existing = connection.ExecuteScalar<long>(
            "SELECT id FROM applications WHERE exe_path = @path", new { path = exePath }, transaction);
        _byPath[exePath] = existing;
        return existing;
    }
}

/// <summary>Запись потока событий в raw_events (батчи, одна транзакция на батч) и чтение журнала.</summary>
public sealed class EventRepository(SqliteConnectionFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions PayloadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public void AppendBatch(IReadOnlyList<ActivityEvent> events, string? batchId = null)
    {
        if (events.Count == 0)
            return;

        using var connection = factory.Open();
        using var tx = connection.BeginTransaction();
        if (batchId is not null && connection.Execute(
            "INSERT INTO committed_batches(id, committed_utc) VALUES (@batchId, @now) ON CONFLICT(id) DO NOTHING",
            new { batchId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx) == 0)
        {
            tx.Commit();
            return;
        }
        var apps = new AppRegistry(); // transaction-local: never retains rolled-back IDs
        foreach (var ev in events)
        {
            long? appId = null;
            if (ev.AppPath is { } path)
                appId = apps.GetOrAdd(connection, path, ev.AppName ?? path, ev.TimestampUtcMs, tx);

            connection.Execute(
                "INSERT INTO raw_events (ts_utc, kind, app_id, window_title, payload) VALUES (@ts, @kind, @appId, @title, @payload)",
                new { ts = ev.TimestampUtcMs, kind = (int)ev.Kind, appId, title = ev.WindowTitle, payload = ev.Payload },
                tx);
        }
        tx.Commit();
    }

    public List<ActivityEvent> GetRange(long fromUtcMs, long toUtcMs)
    {
        using var connection = factory.Open();
        return connection.Query<(long Ts, int Kind, string? ExePath, string? ExeName, string? Title)>(
            """
            SELECT e.ts_utc, e.kind, a.exe_path, a.exe_name, e.window_title
            FROM raw_events e
            LEFT JOIN applications a ON a.id = e.app_id
            WHERE e.ts_utc >= @from AND e.ts_utc < @to
            ORDER BY e.ts_utc, e.id
            """,
            new { from = fromUtcMs, to = toUtcMs })
            .Select(r => new ActivityEvent(r.Ts, (ActivityEventKind)r.Kind, r.ExePath, r.ExeName, r.Title))
            .ToList();
    }

    /// <summary>Replay from the last run boundary, or all legacy state events.</summary>
    public List<ActivityEvent> GetReplayRange(long fromUtcMs, long toUtcMs)
    {
        using var connection = factory.Open();
        var start = connection.ExecuteScalar<long?>(
            "SELECT MAX(ts_utc) FROM raw_events WHERE kind = 18 AND ts_utc <= @from", new { from = fromUtcMs }) ?? long.MinValue;
        return connection.Query<(long Ts, int Kind, string? ExePath, string? ExeName, string? Title)>(
            """
            SELECT e.ts_utc, e.kind, a.exe_path, a.exe_name, e.window_title
            FROM raw_events e LEFT JOIN applications a ON a.id = e.app_id
            WHERE e.ts_utc >= @start AND e.ts_utc < @to
              AND (e.kind BETWEEN 1 AND 9 OR e.kind BETWEEN 18 AND 20)
            ORDER BY e.ts_utc, e.id
            """, new { start, to = toUtcMs })
            .Select(r => new ActivityEvent(r.Ts, (ActivityEventKind)r.Kind, r.ExePath, r.ExeName, r.Title)).ToList();
    }

    /// <summary>
    /// Удалить журнал и сессии старше порога (строго: ts_utc &lt; cutoff, ended_utc &lt; cutoff).
    /// Возвращает число удалённых строк. Checkpoint усекает WAL; свободные страницы
    /// основного файла переиспользуются SQLite, без обещания уменьшить его размер.
    /// </summary>
    public (long Events, long Sessions) DeleteOlderThan(long cutoffUtcMs)
    {
        using var connection = factory.Open();
        using var tx = connection.BeginTransaction();
        var events = connection.Execute("DELETE FROM raw_events WHERE ts_utc < @cutoff",
            new { cutoff = cutoffUtcMs }, tx);
        var sessions = connection.Execute("DELETE FROM sessions WHERE ended_utc < @cutoff",
            new { cutoff = cutoffUtcMs }, tx);
        connection.Execute("DELETE FROM applications WHERE id NOT IN (SELECT app_id FROM raw_events WHERE app_id IS NOT NULL) AND id NOT IN (SELECT app_id FROM sessions WHERE app_id IS NOT NULL)", transaction: tx);
        tx.Commit();

        try { connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);"); }
        catch { /* усечение не критично — файл всё равно не растёт от удаления */ }

        return (events, sessions);
    }

    // --- Журнальные события: процессы и метрики ---

    public sealed record ProcessEventEntry(long TsUtcMs, bool Started, string ExeName, uint Pid, string? ParentName);

    private sealed record ProcessPayloadJson(uint Pid, uint Ppid, string Exe, string? Parent);

    /// <summary>Последние события запуска/остановки процессов, новые сверху.</summary>
    public List<ProcessEventEntry> GetProcessEvents(long fromUtcMs, long toUtcMs, int limit)
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Ts, int Kind, string? Payload)>(
            """
            SELECT ts_utc, kind, payload FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind IN (10, 11)
            ORDER BY ts_utc DESC
            LIMIT @limit
            """,
            new { from = fromUtcMs, to = toUtcMs, limit });

        return rows.Select(r =>
        {
            var json = r.Payload is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<ProcessPayloadJson>(r.Payload, PayloadJson);
            return new ProcessEventEntry(
                r.Ts,
                Started: r.Kind == (int)ActivityEventKind.ProcessStarted,
                json?.Exe ?? "?",
                json?.Pid ?? 0,
                json?.Parent);
        }).ToList();
    }

    public long CountProcessStarts(long fromUtcMs, long toUtcMs)
    {
        using var connection = factory.Open();
        return connection.ExecuteScalar<long>(
            """
            SELECT COUNT(*) FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind = 10
            """,
            new { from = fromUtcMs, to = toUtcMs });
    }

    public sealed record MetricSampleEntry(long TsUtcMs, double CpuPercent, double RamPercent, double RamUsedGb, double RamTotalGb);

    private sealed record MetricsPayloadJson(double Cpu, double RamPct, double RamUsedGb, double RamTotalGb);

    public List<MetricSampleEntry> GetMetricSamples(long fromUtcMs, long toUtcMs)
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Ts, string? Payload)>(
            """
            SELECT ts_utc, payload FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind = 12
            ORDER BY ts_utc
            """,
            new { from = fromUtcMs, to = toUtcMs });

        return rows.Select(r =>
        {
            var json = r.Payload is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<MetricsPayloadJson>(r.Payload, PayloadJson);
            return new MetricSampleEntry(
                r.Ts,
                json?.Cpu ?? 0,
                json?.RamPct ?? 0,
                json?.RamUsedGb ?? 0,
                json?.RamTotalGb ?? 0);
        }).ToList();
    }

    // --- Наблюдение за файлами (kinds 13-16) ---

    public sealed record FileEventEntry(long TsUtcMs, ActivityEventKind Kind, string Path, string? OldPath);

    private sealed record FilePayloadJson(string Path, string? OldPath);

    public List<FileEventEntry> GetFileEvents(long fromUtcMs, long toUtcMs, int limit)
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Ts, int Kind, string? Payload)>(
            """
            SELECT ts_utc, kind, payload FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind IN (13, 14, 15, 16)
            ORDER BY ts_utc DESC
            LIMIT @limit
            """,
            new { from = fromUtcMs, to = toUtcMs, limit });

        return rows.Select(r =>
        {
            var json = r.Payload is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<FilePayloadJson>(r.Payload, PayloadJson);
            return new FileEventEntry(r.Ts, (ActivityEventKind)r.Kind, json?.Path ?? "?", json?.OldPath);
        }).ToList();
    }

    /// <summary>Количество событий по видам за период: (создано, изменено, удалено, переименовано).</summary>
    public (long Created, long Modified, long Deleted, long Renamed) CountFileEvents(long fromUtcMs, long toUtcMs)
    {
        using var connection = factory.Open();
        var counts = connection.Query<(int Kind, long Count)>(
            """
            SELECT kind, COUNT(*) FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind IN (13, 14, 15, 16)
            GROUP BY kind
            """,
            new { from = fromUtcMs, to = toUtcMs })
            .ToDictionary(r => r.Kind, r => r.Count);

        return (
            counts.GetValueOrDefault(13),
            counts.GetValueOrDefault(14),
            counts.GetValueOrDefault(15),
            counts.GetValueOrDefault(16));
    }

    // --- Браузерные визиты (kind 17) ---

    public sealed record BrowserVisitEntry(long TsUtcMs, string Url, string Domain, string Title, string Browser);

    private sealed record BrowserPayloadJson(string Url, string Domain, string Title, string Browser);

    public List<BrowserVisitEntry> GetBrowserVisits(long fromUtcMs, long toUtcMs, int limit)
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Ts, string? Payload)>(
            """
            SELECT ts_utc, payload FROM raw_events
            WHERE ts_utc >= @from AND ts_utc < @to AND kind = 17
            ORDER BY ts_utc DESC
            LIMIT @limit
            """,
            new { from = fromUtcMs, to = toUtcMs, limit });

        return rows.Select(r =>
        {
            var json = r.Payload is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<BrowserPayloadJson>(r.Payload, PayloadJson);
            return new BrowserVisitEntry(
                r.Ts, json?.Url ?? "", json?.Domain ?? "?", json?.Title ?? "", json?.Browser ?? "?");
        }).ToList();
    }
}

/// <summary>Хранение материализованных сессий: замещаем всё, что пересекается с окном.</summary>
public sealed class SessionRepository(SqliteConnectionFactory factory)
{
    public void ReplaceFrom(long fromUtcMs, IReadOnlyList<SessionRow> rows)
    {
        using var connection = factory.Open();
        using var tx = connection.BeginTransaction();
        var apps = new AppRegistry();
        connection.Execute("DELETE FROM sessions WHERE ended_utc > @from", new { from = fromUtcMs }, tx);
        foreach (var row in rows)
        {
            if (row.EndedUtcMs <= row.StartedUtcMs)
                throw new ArgumentException("Session end must be after its start.", nameof(rows));
            long? appId = null;
            if (row.AppPath is { } path)
                appId = apps.GetOrAdd(connection, path, row.AppName ?? path, row.StartedUtcMs, tx);

            connection.Execute(
                """
                INSERT INTO sessions (started_utc, ended_utc, app_id, is_idle, window_title, project, category_name)
                VALUES (@start, @end, @appId, @idle, @title, @project, @category)
                """,
                new
                {
                    start = row.StartedUtcMs,
                    end = row.EndedUtcMs,
                    appId,
                    idle = row.IsIdle ? 1 : 0,
                    title = row.WindowTitle,
                    project = row.Project,
                    category = row.CategoryName,
                },
                tx);
        }
        tx.Commit();
    }

    private sealed record SessionDbRow(
        long started_utc, long ended_utc, long is_idle, string? window_title,
        string? project, string category_name, string? exe_path, string? exe_name);

    public List<SessionRow> GetRange(long fromUtcMs, long toUtcMs, Pat.Core.Categorization.CategoryRuleSet rules)
    {
        using var connection = factory.Open();
        var dbRows = connection.Query<SessionDbRow>(
            """
            SELECT s.started_utc, s.ended_utc, s.is_idle, s.window_title, s.project, s.category_name,
                   a.exe_path, a.exe_name
            FROM sessions s
            LEFT JOIN applications a ON a.id = s.app_id
            WHERE s.ended_utc > @from AND s.started_utc < @to
            ORDER BY s.started_utc
            """,
            new { from = fromUtcMs, to = toUtcMs });

        return dbRows.Select(r =>
        {
            bool isIdle = r.is_idle != 0;
            bool isExcluded = !isIdle && r.exe_path is null;
            // Цвет категории досчитываем на лету по текущим правилам
            string category;
            string color;
            if (isIdle)
            {
                category = "Idle";
                color = "#4B5563";
            }
            else if (isExcluded)
            {
                category = "(исключено)";
                color = "#374151";
            }
            else
            {
                var classified = rules.Classify(r.exe_name, r.exe_path, r.window_title);
                category = classified.Category;
                color = classified.ColorHex;
            }
            return new SessionRow(
                Math.Max(fromUtcMs, r.started_utc), Math.Min(toUtcMs, r.ended_utc),
                r.exe_path, isIdle ? "Idle" : r.exe_name ?? "(исключено)",
                r.window_title, r.project,
                category, color, isIdle, isExcluded);
        }).ToList();
    }
}
