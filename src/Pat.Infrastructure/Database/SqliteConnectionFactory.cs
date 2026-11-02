using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pat.Infrastructure.Database;

/// <summary>
/// Фабрика SQLite-соединений. Писатель один (фоновый поток DbWriter), читателей много (UI) —
/// WAL даёт конкурентное чтение без блокировок.
/// </summary>
public sealed class SqliteConnectionFactory
{
    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """;
            command.ExecuteNonQuery();
        }
        return connection;
    }
}

/// <summary>Применяет встроенные миграции 001_*.sql по порядку, каждая в своей транзакции.</summary>
public sealed class MigrationRunner(SqliteConnectionFactory factory)
{
    public void Run()
    {
        using var connection = factory.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_utc INTEGER NOT NULL)";
            command.ExecuteNonQuery();
        }

        foreach (var (version, sql) in DiscoverMigrations())
        {
            using var tx = connection.BeginTransaction();
            var applied = connection.ExecuteScalar<long?>("SELECT COUNT(*) FROM schema_migrations WHERE version = @v",
                new { v = version }, tx);
            if (applied > 0)
            {
                tx.Commit();
                continue;
            }
            connection.Execute(sql, transaction: tx);
            connection.Execute("INSERT INTO schema_migrations(version, applied_utc) VALUES (@v, @now)",
                new { v = version, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
            tx.Commit();
        }
    }

    private static IEnumerable<(int Version, string Sql)> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("Pat.Infrastructure.Migrations.", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileNameWithoutExtension(name["Pat.Infrastructure.Migrations.".Length..]);
            var version = int.Parse(fileName.Split('_')[0]);
            using var reader = assembly.GetManifestResourceStream(name)!;
            using var text = new StreamReader(reader);
            yield return (version, text.ReadToEnd());
        }
    }
}
