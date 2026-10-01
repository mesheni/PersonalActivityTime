#:package Microsoft.Data.Sqlite@10.0.12

using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalActivityTime", "activity.db");

if (!File.Exists(dbPath))
{
    Console.WriteLine($"База не найдена: {dbPath}");
    return 1;
}

var kindNames = new Dictionary<long, string>
{
    [1] = "Foreground", [2] = "IdleBegan", [3] = "IdleEnded",
    [4] = "Locked", [5] = "Unlocked", [6] = "Suspend", [7] = "Resume",
    [8] = "Paused", [9] = "Resumed",
    [10] = "ProcStarted", [11] = "ProcStopped", [12] = "SysSample",
    [13] = "FileCreated", [14] = "FileMod", [15] = "FileDel", [16] = "FileRenamed", [17] = "BrowserVisit",
};

using var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
cn.Open();

long Count(string sql)
{
    using var cmd = cn.CreateCommand();
    cmd.CommandText = sql;
    return (long)(cmd.ExecuteScalar() ?? 0);
}

Console.WriteLine($"База: {dbPath}");
Console.WriteLine($"applications: {Count("SELECT COUNT(*) FROM applications")}");
Console.WriteLine($"raw_events:   {Count("SELECT COUNT(*) FROM raw_events")}");
Console.WriteLine($"sessions:     {Count("SELECT COUNT(*) FROM sessions")}");
Console.WriteLine();

Console.WriteLine("События по типам:");
using (var cmd = cn.CreateCommand())
{
    cmd.CommandText = "SELECT kind, COUNT(*) FROM raw_events GROUP BY kind ORDER BY kind";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var kind = reader.GetInt64(0);
        Console.WriteLine($"  {kindNames.GetValueOrDefault(kind, kind.ToString())}: {reader.GetInt64(1)}");
    }
}
Console.WriteLine();

Console.WriteLine("Последние события:");
using (var cmd = cn.CreateCommand())
{
    cmd.CommandText = """
        SELECT e.ts_utc, e.kind, a.exe_name, e.window_title, e.payload
        FROM raw_events e LEFT JOIN applications a ON a.id = e.app_id
        ORDER BY e.id DESC LIMIT 12
        """;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime;
        var kind = kindNames.GetValueOrDefault(reader.GetInt64(1), "?");
        var app = reader.IsDBNull(2) ? "" : reader.GetString(2);
        var title = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var payload = reader.IsDBNull(4) ? "" : reader.GetString(4);
        Console.WriteLine($"  {ts:HH:mm:ss}  {kind,-12} {app,-18} {title,-28} {payload}");
    }
}
Console.WriteLine();

Console.WriteLine("Последние сэмплы метрик:");
using (var cmd = cn.CreateCommand())
{
    cmd.CommandText = "SELECT ts_utc, payload FROM raw_events WHERE kind = 12 ORDER BY ts_utc DESC LIMIT 3";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime;
        Console.WriteLine($"  {ts:HH:mm:ss}  {reader.GetString(1)}");
    }
}
Console.WriteLine();

Console.WriteLine("Сессии:");
using (var cmd = cn.CreateCommand())
{
    cmd.CommandText = """
        SELECT s.started_utc, s.ended_utc, s.is_idle, s.category_name, a.exe_name
        FROM sessions s LEFT JOIN applications a ON a.id = s.app_id
        ORDER BY s.started_utc LIMIT 12
        """;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var st = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime;
        var en = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).LocalDateTime;
        var app = reader.IsDBNull(4) ? (reader.GetInt64(2) != 0 ? "Idle" : "(исключено)") : reader.GetString(4);
        Console.WriteLine($"  {st:HH:mm:ss}-{en:HH:mm:ss}  {(en - st).TotalSeconds,6:F0}с  {reader.GetString(3),-16} {app}");
    }
}
return 0;
