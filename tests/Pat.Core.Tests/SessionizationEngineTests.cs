using Pat.Core.Events;

namespace Pat.Core.Tests;

public sealed class SessionizationEngineTests
{
    private static readonly long T0 = 1_700_000_000_000; // условный "день"
    private static long Ms(int minutes) => T0 + minutes * 60_000L;

    private readonly Pat.Core.Sessionization.SessionizationEngine _engine = new();

    private IReadOnlyList<Pat.Core.Sessionization.ActivitySegment> Build(
        params ActivityEvent[] events) =>
        _engine.Build(events, Ms(120));

    [Fact]
    public void SingleForegroundEvent_ProducesSegmentTillBoundary()
    {
        var segments = Build(ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "WaferAnalyzer.sln"));

        var seg = Assert.Single(segments);
        Assert.Equal(Ms(0), seg.StartUtcMs);
        Assert.Equal(Ms(120), seg.EndUtcMs);
        Assert.Equal("rider64", seg.AppName);
        Assert.False(seg.IsIdle);
    }

    [Fact]
    public void AppSwitch_SplitsIntoTwoSegments()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "WaferAnalyzer.sln"),
            ActivityEvent.Foreground(Ms(30), @"C:\chrome.exe", "chrome", "GitHub"));

        Assert.Equal(2, segments.Count);
        Assert.Equal(Ms(0), segments[0].StartUtcMs);
        Assert.Equal(Ms(30), segments[0].EndUtcMs);
        Assert.Equal(Ms(30), segments[1].StartUtcMs);
        Assert.Equal(Ms(120), segments[1].EndUtcMs);
    }

    [Fact]
    public void IdleBegan_AtLastInputTimestamp_CutsActiveSegment()
    {
        // Порог 5 минут, заметили в 10-й, но последний ввод был в 8-й
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, Ms(8)),
            ActivityEvent.System(ActivityEventKind.IdleEnded, Ms(20)));

        Assert.Equal(3, segments.Count); // rider, idle, rider (то же окно переоткрылось)
        Assert.Equal(Ms(0), segments[0].StartUtcMs);
        Assert.Equal(Ms(8), segments[0].EndUtcMs); // активная часть кончилась по последнему вводу

        Assert.True(segments[1].IsIdle);
        Assert.Equal(Ms(8), segments[1].StartUtcMs);
        Assert.Equal(Ms(20), segments[1].EndUtcMs);
    }

    [Fact]
    public void IdleEnded_ReopensSameForeground_WithoutNewForegroundEvent()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, Ms(8)),
            ActivityEvent.System(ActivityEventKind.IdleEnded, Ms(20)));

        // После возврата то же окно продолжает считаться активным без нового события
        var seg = segments.Last();
        Assert.False(seg.IsIdle);
        Assert.Equal("rider64", seg.AppName);
        Assert.Equal(Ms(20), seg.StartUtcMs);
        Assert.Equal(Ms(120), seg.EndUtcMs);
    }

    [Fact]
    public void LockDuringIdle_ThenUnlock_IdleEndsAndWindowReopens()
    {
        // IdleEnded и Unlocked приходят с одной меткой: ввод в сессии после разблокировки
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs"),
            ActivityEvent.System(ActivityEventKind.IdleBegan, Ms(8)),
            ActivityEvent.System(ActivityEventKind.SessionLocked, Ms(15)),
            ActivityEvent.System(ActivityEventKind.IdleEnded, Ms(40)),
            ActivityEvent.System(ActivityEventKind.SessionUnlocked, Ms(40)));

        // активный 0–8, idle 8–15, залочено 15–40 (дыра), после unlock окно активно
        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsIdle);
        Assert.True(segments[1].IsIdle);
        Assert.Equal((Ms(8), Ms(15)), (segments[1].StartUtcMs, segments[1].EndUtcMs));
        Assert.False(segments[2].IsIdle);
        Assert.Equal("rider64", segments[2].AppName);
        Assert.Equal((Ms(40), Ms(120)), (segments[2].StartUtcMs, segments[2].EndUtcMs));
    }

    [Fact]
    public void Unlock_ReopensLastForeground()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\chrome.exe", "chrome", "docs"),
            ActivityEvent.System(ActivityEventKind.SessionLocked, Ms(10)),
            ActivityEvent.System(ActivityEventKind.SessionUnlocked, Ms(30)));

        Assert.Equal(2, segments.Count);
        Assert.Equal("chrome", segments[1].AppName);
        Assert.Equal(Ms(30), segments[1].StartUtcMs);
    }

    [Fact]
    public void ShortTrackingPause_IsNotCountedAsActivity()
    {
        // Even a short explicit pause must remain a gap in measured time.
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs — Wafer — Rider"),
            ActivityEvent.System(ActivityEventKind.TrackingPaused, Ms(10)),
            ActivityEvent.System(ActivityEventKind.TrackingResumed, Ms(10) + 3000),
            ActivityEvent.Foreground(Ms(50), @"C:\chrome.exe", "chrome", "GitHub"));

        Assert.Equal(3, segments.Count);
        Assert.Equal("rider64", segments[0].AppName);
        Assert.Equal(Ms(10), segments[0].EndUtcMs);
        Assert.Equal(Ms(10) + 3000, segments[1].StartUtcMs);
        Assert.Equal("chrome", segments[2].AppName);
    }

    [Fact]
    public void SameAppDifferentTitle_NotMerged()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs — Rider"),
            ActivityEvent.Foreground(Ms(1), @"C:\rider.exe", "rider64", "Main.cs — Rider"));

        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void TitleNormalization_ExtraSpacesStillMerges()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs  —  Rider"),
            ActivityEvent.Foreground(Ms(2), @"C:\rider.exe", "rider64", "Parser.cs — Rider"));

        var seg = Assert.Single(segments);
        Assert.Equal(Ms(120), seg.EndUtcMs);
    }

    [Fact]
    public void ExcludedForeground_TrackedWithoutDetails()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs"),
            ActivityEvent.ExcludedForeground(Ms(10)),
            ActivityEvent.Foreground(Ms(15), @"C:\rider.exe", "rider64", "Parser.cs"));

        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsExcluded);
        Assert.Null(segments[1].AppPath);
        Assert.Equal("(исключено)", segments[1].AppName);
        Assert.False(segments[2].IsExcluded);
    }

    [Fact]
    public void EventsOutOfOrder_AreSortedBeforeProcessing()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(30), @"C:\chrome.exe", "chrome", "GitHub"),
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Wafer"));

        Assert.Equal(2, segments.Count);
        Assert.Equal("rider64", segments[0].AppName);
    }

    [Fact]
    public void EmptyStream_NoSegments()
    {
        Assert.Empty(Build());
    }

    [Fact]
    public void JournalEvents_ProcessesMetricsFilesBrowser_DoNotAffectSessions()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Wafer"),
            ActivityEvent.Journal(ActivityEventKind.ProcessStarted, Ms(1), """{"pid":1}"""),
            ActivityEvent.Journal(ActivityEventKind.SystemSample, Ms(2), """{"cpu":12}"""),
            ActivityEvent.Journal(ActivityEventKind.FileModified, Ms(3), """{"path":"C:\\x\\a.cs"}"""),
            ActivityEvent.Journal(ActivityEventKind.FileRenamed, Ms(4), """{"path":"C:\\x\\b.cs","oldPath":"C:\\x\\a.cs"}"""),
            ActivityEvent.Journal(ActivityEventKind.BrowserVisit, Ms(5), """{"url":"https://github.com","domain":"github.com","title":"GH","browser":"chrome"}"""),
            ActivityEvent.Journal(ActivityEventKind.ProcessStopped, Ms(6), """{"pid":1}"""));

        var seg = Assert.Single(segments);
        Assert.Equal("rider64", seg.AppName);
        Assert.Equal((Ms(0), Ms(120)), (seg.StartUtcMs, seg.EndUtcMs));
    }

    [Fact]
    public void UnknownFutureKind_IsIgnoredNotThrown()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Wafer"),
            new ActivityEvent(Ms(1), (ActivityEventKind)99));

        Assert.Single(segments);
    }

    [Fact]
    public void SuspendResume_CreatesGapAndReopens()
    {
        var segments = Build(
            ActivityEvent.Foreground(Ms(0), @"C:\rider.exe", "rider64", "Parser.cs"),
            ActivityEvent.System(ActivityEventKind.SystemSuspend, Ms(10)),
            ActivityEvent.System(ActivityEventKind.SystemResume, Ms(90)));

        Assert.Equal(2, segments.Count);
        Assert.Equal(Ms(10), segments[0].EndUtcMs);
        Assert.Equal((Ms(90), Ms(120)), (segments[1].StartUtcMs, segments[1].EndUtcMs));
    }
}
