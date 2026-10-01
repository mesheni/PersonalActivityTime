namespace Pat.Core.Sessionization;

/// <summary>
/// Готовый отрезок активности: приложение было на переднем плане с Start по End.
/// Idle-сегмент — пользователь не вводил ничего (AppName = "Idle").
/// Excluded-сегмент — приложение в списке исключений, детали не записаны.
/// </summary>
public sealed record ActivitySegment(
    long StartUtcMs,
    long EndUtcMs,
    string? AppPath,
    string AppName,
    string? WindowTitle,
    bool IsIdle,
    bool IsExcluded)
{
    public double DurationSeconds => (EndUtcMs - StartUtcMs) / 1000.0;

    public ActivitySegment ClipTo(long fromUtcMs, long toUtcMs)
    {
        var start = Math.Max(StartUtcMs, fromUtcMs);
        var end = Math.Min(EndUtcMs, toUtcMs);
        return (start, end) == (StartUtcMs, EndUtcMs) ? this : this with { StartUtcMs = start, EndUtcMs = end };
    }
}
