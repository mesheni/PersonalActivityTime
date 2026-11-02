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
        bool locked = false, suspended = false, paused = false, stopped = false;
        bool Blocked() => locked || suspended || paused || stopped;

        var ordered = events.Where(e => e.TimestampUtcMs <= endBoundaryUtcMs)
            .OrderBy(e => e.TimestampUtcMs).ToList();
        // A later start proves the previous run ended. Without a stop, only its
        // last heartbeat is known. The current run gets a short, bounded lease.
        var runEnds = new Dictionary<int, long>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Kind != ActivityEventKind.TrackingStarted) continue;
            var confirmed = ordered[i].TimestampUtcMs;
            var j = i + 1;
            for (; j < ordered.Count && ordered[j].Kind != ActivityEventKind.TrackingStarted; j++)
            {
                if (ordered[j].Kind == ActivityEventKind.TrackingHeartbeat)
                    confirmed = ordered[j].TimestampUtcMs;
                if (ordered[j].Kind == ActivityEventKind.TrackingStopped)
                {
                    confirmed = ordered[j].TimestampUtcMs;
                    break;
                }
            }
            runEnds[i] = j < ordered.Count ? confirmed : Math.Min(endBoundaryUtcMs, confirmed + 15_000);
        }
        long runEnd = endBoundaryUtcMs;

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
            if (!Blocked() && idleStart is null && current is null && lastForeground is { } fg)
                current = fg.ToStartOfSegment() with { StartUtcMs = startUtcMs };
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var ev = ordered[index];
            if (ev.TimestampUtcMs > runEnd)
            {
                CloseActive(runEnd);
                CloseIdle(runEnd);
                stopped = true;
            }
            switch (ev.Kind)
            {
                case ActivityEventKind.TrackingStarted:
                    CloseActive(Math.Min(ev.TimestampUtcMs, runEnd));
                    CloseIdle(Math.Min(ev.TimestampUtcMs, runEnd));
                    lastForeground = null;
                    locked = suspended = paused = stopped = false;
                    runEnd = runEnds[index];
                    break;
                case ActivityEventKind.TrackingStopped:
                    CloseActive(ev.TimestampUtcMs);
                    CloseIdle(ev.TimestampUtcMs);
                    stopped = true;
                    lastForeground = null;
                    break;
                case ActivityEventKind.ForegroundChanged:
                    // A title change is context, not evidence of user input.
                    CloseActive(ev.TimestampUtcMs);
                    lastForeground = ev;
                    ReopenForeground(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.IdleBegan:
                    CloseActive(ev.TimestampUtcMs);
                    if (!Blocked()) idleStart ??= ev.TimestampUtcMs;
                    break;

                case ActivityEventKind.IdleEnded:
                    CloseIdle(ev.TimestampUtcMs);
                    ReopenForeground(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.SessionLocked:
                case ActivityEventKind.SystemSuspend:
                case ActivityEventKind.TrackingPaused:
                    if (ev.Kind == ActivityEventKind.SessionLocked) locked = true;
                    if (ev.Kind == ActivityEventKind.SystemSuspend) suspended = true;
                    if (ev.Kind == ActivityEventKind.TrackingPaused) paused = true;
                    CloseActive(ev.TimestampUtcMs);
                    CloseIdle(ev.TimestampUtcMs);
                    break;

                case ActivityEventKind.SessionUnlocked:
                case ActivityEventKind.SystemResume:
                case ActivityEventKind.TrackingResumed:
                    if (ev.Kind == ActivityEventKind.SessionUnlocked) locked = false;
                    if (ev.Kind == ActivityEventKind.SystemResume) suspended = false;
                    if (ev.Kind == ActivityEventKind.TrackingResumed) paused = false;
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

        CloseActive(Math.Min(endBoundaryUtcMs, runEnd));
        CloseIdle(Math.Min(endBoundaryUtcMs, runEnd));

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
        // Never count a real pause/lock gap as active time.
        if (next.StartUtcMs != prev.EndUtcMs) return false;
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
