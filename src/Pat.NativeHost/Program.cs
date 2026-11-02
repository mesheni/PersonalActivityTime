using System.Buffers.Binary;
using System.Text.Json;
using Pat.Core.Events;
using Pat.Infrastructure.Browsers;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;

namespace Pat.NativeHost;

/// <summary>
/// Хост native messaging для браузерного расширения. Chrome запускает процесс и
/// передаёт сообщения через stdin в формате: 4 байта длины (uint32 LE) + UTF-8 JSON.
/// Первое сообщение — рукопожатие (chrome_extension_id и т.п.), оно не содержит url
/// и молча пропускается. Ответы в stdout не пишутся.
/// Визиты пишутся прямо в ту же SQLite (WAL держит двух писателей, busy_timeout
/// переживает пересечения с батчами приложения). Пока трекинг на паузе (файл-маркер),
/// запись пропускается.
/// </summary>
internal static class Program
{
    private const uint MaxMessageBytes = 1024 * 1024;

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalActivityTime");

    private static readonly string PausedMarkerPath = Path.Combine(AppDataDir, "tracking.paused");
    private static readonly FileLog LogFile = new("nativehost.log");

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object DbGate = new();
    private static EventRepository? _events;

    // Дедупликация: расширение может прислать одно и то же из onUpdated и onFocusChanged
    private static string _lastUrl = "";
    private static string _lastTitle = "";
    private static long _lastTs;

    private sealed record IncomingMessage(string? Url, string? Title, string? Browser);

    private sealed record VisitPayload(string Url, string Domain, string Title, string Browser);

    private static int Main()
    {
        using var stdin = Console.OpenStandardInput();

        // Схема на случай, если приложение ещё ни разу не запускалось
        try
        {
            new MigrationRunner(new SqliteConnectionFactory(Path.Combine(AppDataDir, "activity.db"))).Run();
        }
        catch (Exception ex)
        {
            Log("migration failed: " + ex.Message);
        }

        while (TryReadMessage(stdin, out var json))
        {
            try
            {
                Handle(json);
            }
            catch (Exception ex)
            {
                Log("handle failed: " + ex.Message);
            }
        }
        return 0;
    }

    private static void Handle(string json)
    {
        var message = TryParse(json);
        if (message is null)
            return;

        var store = new Pat.Infrastructure.Settings.SettingsStore();
        var settings = store.Load();
        var browser = message.Browser == "msedge" ? "msedge" : "chrome";
        if (store.LastError is not null || Pat.Infrastructure.Settings.PrivacyPolicy.IsExcluded(settings, browser)) return;

        // Рукопожатие Chrome и не-web схемы пропускаются
        if (!UrlNormalizer.TryExtractDomain(message.Url, out var domain))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var title = message.Title is { Length: > 300 } longTitle ? longTitle[..300] : message.Title ?? "";
        var url = message.Url ?? "";

        if (url == _lastUrl && title == _lastTitle && now - _lastTs < 1500)
            return;
        _lastUrl = url;
        _lastTitle = title;
        _lastTs = now;

        if (File.Exists(PausedMarkerPath))
            return; // трекинг на паузе — не пишем

        var payload = JsonSerializer.Serialize(new VisitPayload(url, domain, title, browser), PayloadJson);
        var ev = ActivityEvent.Journal(ActivityEventKind.BrowserVisit, now, payload);

        lock (DbGate)
        {
            (_events ??= new EventRepository(new SqliteConnectionFactory(Path.Combine(AppDataDir, "activity.db"))))
                .AppendBatch([ev]);
        }
    }

    private static IncomingMessage? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IncomingMessage>(json, PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadMessage(Stream stdin, out string json)
    {
        json = "";
        var lengthBytes = new byte[4];
        if (!ReadExactly(stdin, lengthBytes))
            return false;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length == 0 || length > MaxMessageBytes)
            return false;

        var body = new byte[length];
        if (!ReadExactly(stdin, body))
            return false;

        json = System.Text.Encoding.UTF8.GetString(body);
        return true;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                return false; // EOF: браузер отключился — выходим
            offset += read;
        }
        return true;
    }

    private static void Log(string message) => LogFile.Write(message);
}
