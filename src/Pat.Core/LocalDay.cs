namespace Pat.Core;

/// <summary>Границы локальных суток в UTC-миллисекундах. Храним UTC, дни считаем по местному времени.</summary>
public static class LocalDay
{
    public static (long StartUtcMs, long EndUtcMs) Bounds(DateTime localDate, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var dateOnly = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        var start = TimeZoneInfo.ConvertTimeToUtc(dateOnly, tz);
        return (
            new DateTimeOffset(start).ToUnixTimeMilliseconds(),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(dateOnly.AddDays(1), tz)).ToUnixTimeMilliseconds());
    }

    public static (long StartUtcMs, long EndUtcMs) Today(TimeZoneInfo? timeZone = null) =>
        Bounds(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone ?? TimeZoneInfo.Local).DateTime, timeZone);
}
