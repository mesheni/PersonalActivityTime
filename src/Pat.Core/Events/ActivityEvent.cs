using Pat.Core.Sessionization;

namespace Pat.Core.Events;

/// <summary>
/// Атомарное событие активности. Пишется в append-only журнал raw_events
/// и является источником истины для пересборки сессий.
/// </summary>
/// <param name="AppPath">Полный путь к exe. null для исключённых приложений (приватность).</param>
/// <param name="Payload">JSON для журнальных событий (процессы, метрики).</param>
public sealed record ActivityEvent(
    long TimestampUtcMs,
    ActivityEventKind Kind,
    string? AppPath = null,
    string? AppName = null,
    string? WindowTitle = null,
    string? Payload = null)
{
    public static ActivityEvent Foreground(long tsUtcMs, string appPath, string appName, string title) =>
        new(tsUtcMs, ActivityEventKind.ForegroundChanged, appPath, appName, title);

    /// <summary>Маркер активного окна из списка исключений: имя и заголовок не записываем.</summary>
    public static ActivityEvent ExcludedForeground(long tsUtcMs) =>
        new(tsUtcMs, ActivityEventKind.ForegroundChanged);

    public static ActivityEvent System(ActivityEventKind kind, long tsUtcMs) =>
        new(tsUtcMs, kind);

    /// <summary>Журнальное событие (процессы, метрики): только kind + JSON payload.</summary>
    public static ActivityEvent Journal(ActivityEventKind kind, long tsUtcMs, string payloadJson) =>
        new(tsUtcMs, kind, Payload: payloadJson);

    /// <summary>Сегмент, порождённый этим событием, считается исключённым (AppPath не записан).</summary>
    public bool IsExcluded => Kind == ActivityEventKind.ForegroundChanged && AppPath is null;

    public ActivitySegment ToStartOfSegment() => new(
        TimestampUtcMs,
        TimestampUtcMs,
        AppPath,
        AppName ?? "(исключено)",
        WindowTitle,
        IsIdle: false,
        IsExcluded: IsExcluded);
}
