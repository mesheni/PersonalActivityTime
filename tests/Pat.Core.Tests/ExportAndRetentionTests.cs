using System.Text.Json;
using Pat.Core.Events;
using Pat.Core.Export;
using Pat.Infrastructure.Database;
using Xunit;

namespace Pat.Core.Tests;

/// <summary>Форматы экспорта: CSV должен открываться в Excel, JSON — разбираться обратно.</summary>
public sealed class ActivityExporterTests
{
    [Fact]
    public void Csv_QuotesSeparatorQuotesAndNewlines()
    {
        var sessions = new List<ExportSession>
        {
            new(0, 600_000, "devenv", "Работа; «важно» \"quote\"", "Proj", "Программирование", IsIdle: false, IsExcluded: false),
            new(600_000, 900_000, "Idle", null, null, "Idle", IsIdle: true, IsExcluded: false),
        };

        var csv = ActivityExporter.ToCsv(sessions);

        Assert.StartsWith("start;end;minutes;category;app;project;window_title;idle;excluded", csv);
        var lines = csv.TrimEnd('\r', '\n').Split('\n');
        Assert.Equal(3, lines.Length);

        // Разделитель и кавычки внутри заголовка экранированы по RFC 4180
        Assert.Contains("\"Работа; «важно» \"\"quote\"\"\"", lines[1]);
        Assert.EndsWith(";0;0", lines[1].TrimEnd('\r'));
        Assert.EndsWith(";1;0", lines[2].TrimEnd('\r'));

        // Минуты — инвариантная культура (точка), 600 000 мс = 10.0 мин
        Assert.Contains(";10.0;", lines[1]);
        Assert.Contains(";5.0;", lines[2]);
    }

    [Fact]
    public void Csv_EmptyList_ProducesHeaderOnly()
    {
        var csv = ActivityExporter.ToCsv([]);
        Assert.EndsWith("\n", csv, StringComparison.Ordinal);
        Assert.Single(csv.TrimEnd('\r', '\n').Split('\n'));
    }

    [Fact]
    public void Json_ParsesBackWithCamelCaseFields()
    {
        var sessions = new List<ExportSession>
        {
            new(0, 1_800_000, "rider64", "Parser.cs — Wafer", "Wafer", "Программирование", IsIdle: false, IsExcluded: false),
            new(1_800_000, 2_100_000, "(исключено)", null, null, "(исключено)", IsIdle: false, IsExcluded: true),
        };

        var json = ActivityExporter.ToJson(sessions);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("sessionCount").GetInt32());
        Assert.Equal(35.0, root.GetProperty("totalMinutes").GetDouble(), precision: 1);

        var first = root.GetProperty("sessions")[0];
        Assert.Equal("rider64", first.GetProperty("app").GetString());
        Assert.Equal("Wafer", first.GetProperty("project").GetString());
        Assert.Equal("Программирование", first.GetProperty("category").GetString());
        Assert.False(first.GetProperty("idle").GetBoolean());

        var second = root.GetProperty("sessions")[1];
        Assert.True(second.GetProperty("excluded").GetBoolean());
        Assert.Null(second.GetProperty("title").GetString());
    }
}

/// <summary>Retention: удаляется всё строго старше порога, пограничное событие остаётся.</summary>
public sealed class RetentionTests : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly EventRepository _events;

    public RetentionTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pat-retention-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(dbPath);
        new MigrationRunner(_factory).Run();
        _events = new EventRepository(_factory);
    }

    public void Dispose()
    {
        try { File.Delete(_factory.DatabasePath); } catch { /* временная база, не важно */ }
        try { File.Delete(_factory.DatabasePath + "-wal"); } catch { }
        try { File.Delete(_factory.DatabasePath + "-shm"); } catch { }
    }

    [Fact]
    public void DeleteOlderThan_RemovesOnlyStrictlyOlderRows()
    {
        const long cutoff = 1_000_000L;
        _events.AppendBatch(
        [
            ActivityEvent.Journal(ActivityEventKind.FileCreated, cutoff - 1, """{"path":"old.txt"}"""),
            ActivityEvent.Journal(ActivityEventKind.FileCreated, cutoff, """{"path":"edge.txt"}"""),
            ActivityEvent.Journal(ActivityEventKind.FileCreated, cutoff + 1, """{"path":"new.txt"}"""),
        ]);

        var (deletedEvents, deletedSessions) = _events.DeleteOlderThan(cutoff);

        Assert.Equal(1, deletedEvents);
        Assert.Equal(0, deletedSessions);

        var remaining = _events.GetRange(cutoff - 10, cutoff + 10);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.Payload?.Contains("old.txt") == true);
    }

    [Fact]
    public void DeleteOlderThan_IsSafeOnEmptyDatabase()
    {
        var (deletedEvents, deletedSessions) = _events.DeleteOlderThan(0);
        Assert.Equal(0, deletedEvents);
        Assert.Equal(0, deletedSessions);
    }
}
