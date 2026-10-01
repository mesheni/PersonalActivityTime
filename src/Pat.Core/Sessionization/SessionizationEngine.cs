using Pat.Core.Events;

namespace Pat.Core.Sessionization;

/// <summary>
/// Свёртка журнала событий в непрерывные сегменты активности. Чистая детерминированная
/// функция: одни и те же события всегда дают один и тот же результат, поэтому сессии
/// можно пересчитывать при смене правил категоризации.
///
/// Инварианты потока:
///  - IdleBegan приходит с меткой момента последнего ввода, IdleEnded — момента нового;
///  - во время блокировки/сна/паузы активных сегментов нет;
///  - после IdleEnded/Unlocked/Resume/TrackingResumed последний известный передний план
///    открывается заново (окно не менялось, новое ForegroundChanged может не прийти).
/// </summary>
public sealed class SessionizationEngine
{
    public IReadOnlyList<ActivitySegment> Build(
        IEnumerable<ActivityEvent> events,
        long endBoundaryUtcMs,
        SessionizationOptions? options = null)
    {
        var opts = options ?? SessionizationOptions.Default;
        var result = new List<ActivitySegment>();

        ActivityEvent? lastForeground = null;
        ActivitySegment? current = null;
        long? idleStart = null;

        void CloseActive(long endUtcMs)
        {
            if (current is { } seg && endUtcMs > seg.StartUtcMs)
                result.Add(seg with { EndUtcMs = endUtcMs });
            current = null;
        }

        void CloseIdle(long endUtcMs)
        {
            if (idleStart is { } start && endUtcMs > start)
                result.Add(new ActivitySegment(start, endUtcMs, null, "Idle", null, IsIdle: true, IsExcluded: false));
            idleStart = null;
        }

        void ReopenForeground(long startUtcMs)
        {
            if (current is null && lastForeground is { } fg)
                current = fg.ToStartOfSegment() with { StartUtcMs = startUtcMs };
        }

        foreach (var ev in events.OrderBy(e => e.TimestampUtcMs))
        {
            switch (ev.Kind)
            {
                case ActivityEventKind.ForegroundChanged:
                    // Смена окна руками пользователя означает конец простоя.
                    CloseIdle(ev.TimestampUtcMs);
                    CloseActive(ev.TimestampUtcMs);
                    lastForeground = ev;
                    current = ev.ToStartOfSegment();
                    break;

                case ActivityEventKind.IdleBegan:
                    CloseActive(ev.TimestampUtcMs);
                    idleStart ??= ev.TimestampUtcMs;
                    break;

                case ActivityEventKind.IdleEnded:
                    CloseIdle(ev.TimestampUtcMs);
                    ReopenForeground(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.SessionLocked:
                case ActivityEventKind.SystemSuspend:
                case ActivityEventKind.TrackingPaused:
                    CloseActive(ev.TimestampUtcMs);
                    CloseIdle(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.SessionUnlocked:
                case ActivityEventKind.SystemResume:
                case ActivityEventKind.TrackingResumed:
                    CloseIdle(ev.TimestampUtcMs);
                    ReopenForeground(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.ProcessStarted:
                case ActivityEventKind.ProcessStopped:
                case ActivityEventKind.SystemSample:
                case ActivityEventKind.FileCreated:
                case ActivityEventKind.FileModified:
                case ActivityEventKind.FileDeleted:
                case ActivityEventKind.FileRenamed:
                case ActivityEventKind.BrowserVisit:
                    // Журнальные события: полезны для аналитики, на границы сессий не влияют
                    break;

                default:
                    // Неизвестный вид из будущей версии — пропускаем, чтобы материализатор
                    // не падал на журнале, записанном более новой версией приложения
                    break;
            }
        }

        CloseActive(endBoundaryUtcMs);
        CloseIdle(endBoundaryUtcMs);

        return MergeAdjacent(result, opts);
    }

    /// <summary>Склейка соседних сегментов одного вида с крошечным разрывом.</summary>
    private static List<ActivitySegment> MergeAdjacent(List<ActivitySegment> segments, SessionizationOptions opts)
    {
        var merged = new List<ActivitySegment>(segments.Count);
        foreach (var seg in segments.OrderBy(s => s.StartUtcMs))
        {
            if (merged.Count > 0 && CanMerge(merged[^1], seg, opts.MergeGapMs))
                merged[^1] = merged[^1] with { EndUtcMs = Math.Max(merged[^1].EndUtcMs, seg.EndUtcMs) };
            else
                merged.Add(seg);
        }
        return merged;
    }

    private static bool CanMerge(ActivitySegment prev, ActivitySegment next, int mergeGapMs)
    {
        if (prev.IsIdle != next.IsIdle)
            return false;
        if (prev.IsIdle)
            return next.StartUtcMs - prev.EndUtcMs <= mergeGapMs;

        return prev.IsExcluded == next.IsExcluded
            && prev.AppPath == next.AppPath
            && NormalizeTitle(prev.WindowTitle) == NormalizeTitle(next.WindowTitle)
            && next.StartUtcMs - prev.EndUtcMs <= mergeGapMs;
    }

    internal static string? NormalizeTitle(string? title) =>
        title is null ? null : string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
