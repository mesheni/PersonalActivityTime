namespace Pat.Core;

/// <summary>Границы локальных суток в UTC-миллисекундах. Храним UTC, дни считаем по местному времени.</summary>
public static class LocalDay
{
    public static (long StartUtcMs, long EndUtcMs) Bounds(DateTime localDate, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var dateOnly = localDate.Date;
        var start = TimeZoneInfo.ConvertTimeToUtc(dateOnly, tz);
        return (
            new DateTimeOffset(start).ToUnixTimeMilliseconds(),
            new DateTimeOffset(start.AddDays(1)).ToUnixTimeMilliseconds());
    }

    public static (long StartUtcMs, long EndUtcMs) Today(TimeZoneInfo? timeZone = null) =>
        Bounds(DateTimeOffset.Now.LocalDateTime, timeZone);
}
