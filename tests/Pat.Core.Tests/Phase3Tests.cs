using Pat.Core.Events;
using Pat.Infrastructure.Browsers;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;

namespace Pat.Core.Tests;

public sealed class FileEventBufferTests
{
    private static long T = 1_700_000_000_000;

    [Fact]
    public void MultipleChangesPerPath_DebounceToSingleEvent()
    {
        var buffer = new FileEventBuffer();
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileModified, @"C:\p\a.cs", null, T));
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileModified, @"C:\p\a.cs", null, T + 10));
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileModified, @"C:\p\a.cs", null, T + 20));

        var flushed = buffer.Flush();
        var ev = Assert.Single(flushed);
        Assert.Equal(ActivityEventKind.FileModified, ev.Kind);
        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public void StrongerEventWins_WeakerDoesNotOverwrite()
    {
        var buffer = new FileEventBuffer();

        // Переименовали, потом метаданные дёрнулись — должно остаться Renamed с OldPath
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileRenamed, @"C:\p\b.cs", @"C:\p\a.cs", T));
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileModified, @"C:\p\b.cs", null, T + 10));

        var ev = Assert.Single(buffer.Flush());
        Assert.Equal(ActivityEventKind.FileRenamed, ev.Kind);
        Assert.Equal(@"C:\p\a.cs", ev.OldPath);
    }

    [Fact]
    public void DeleteOverridesEverything()
    {
        var buffer = new FileEventBuffer();
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileCreated, @"C:\p\a.cs", null, T));
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileDeleted, @"C:\p\a.cs", null, T + 10));

        var ev = Assert.Single(buffer.Flush());
        Assert.Equal(ActivityEventKind.FileDeleted, ev.Kind);
    }

    [Fact]
    public void DifferentPathsStaySeparate()
    {
        var buffer = new FileEventBuffer();
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileCreated, @"C:\p\a.cs", null, T));
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileCreated, @"C:\p\b.cs", null, T));

        Assert.Equal(2, buffer.Flush().Count);
    }

    [Fact]
    public void FlushCanBeCalledTwice()
    {
        var buffer = new FileEventBuffer();
        buffer.Add(new BufferedFileEvent(ActivityEventKind.FileModified, @"C:\p\a.cs", null, T));
        Assert.Single(buffer.Flush());
        Assert.Empty(buffer.Flush());
    }
}

public sealed class FileIgnoreFilterTests
{
    [Theory]
    [InlineData(@"C:\proj\bin\Debug\app.dll", true)]
    [InlineData(@"C:\proj\obj\x\y.json", true)]
    [InlineData(@"C:\web\node_modules\pkg\index.js", true)]
    [InlineData(@"C:\repo\.git\index", true)]
    [InlineData(@"C:\proj\temp\data.txt", true)]
    [InlineData(@"C:\Users\mesh\AppData\Local\PersonalActivityTime\activity.db", true)]
    [InlineData(@"C:\doc\report.docx~$", true)]
    [InlineData(@"C:\doc\~$report.docx", true)]
    [InlineData(@"C:\doc\note.tmp", true)]
    [InlineData(@"C:\proj\src\Parser.cs", false)]
    [InlineData(@"C:\proj\README.md", false)]
    [InlineData(@"C:\web\src\index.js", false)]
    public void ShouldIgnore_ClassifiesPaths(string path, bool expected)
    {
        Assert.Equal(expected, FileActivityCollector.ShouldIgnore(path));
    }
}

public sealed class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://github.com/user/repo", "github.com")]
    [InlineData("http://www.GitHub.COM/x", "github.com")]
    [InlineData("https://learn.microsoft.com/en-us/dotnet/", "learn.microsoft.com")]
    public void WebUrls_ExtractDomain(string url, string expected)
    {
        Assert.True(UrlNormalizer.TryExtractDomain(url, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("chrome://extensions/")]
    [InlineData("file:///C:/doc.pdf")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    [InlineData(null)]
    [InlineData("")]
    public void NonWebUrls_Rejected(string? url)
    {
        Assert.False(UrlNormalizer.TryExtractDomain(url, out _));
    }
}

/// <summary>Файловые и браузерные события: payload round-trip через реальную SQLite.</summary>
public sealed class FileAndBrowserRepositoryTests : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly EventRepository _events;

    public FileAndBrowserRepositoryTests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pat-test-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(dbPath);
        new MigrationRunner(_factory).Run();
        _events = new EventRepository(_factory);
    }

    public void Dispose()
    {
        try { File.Delete(_factory.DatabasePath); } catch { /* временная база */ }
    }

    [Fact]
    public void FileEvents_RoundTripAndCounts()
    {
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _events.AppendBatch(
        [
            ActivityEvent.Journal(ActivityEventKind.FileCreated, t0, """{"path":"C:\\p\\a.cs","oldPath":null}"""),
            ActivityEvent.Journal(ActivityEventKind.FileModified, t0 + 1000, """{"path":"C:\\p\\a.cs","oldPath":null}"""),
            ActivityEvent.Journal(ActivityEventKind.FileRenamed, t0 + 2000, """{"path":"C:\\p\\b.cs","oldPath":"C:\\p\\a.cs"}"""),
            ActivityEvent.Journal(ActivityEventKind.FileDeleted, t0 + 3000, """{"path":"C:\\p\\b.cs","oldPath":null}"""),
            ActivityEvent.Foreground(t0, @"C:\rider.exe", "rider64", "x"), // не файловое, в счётчик не попадёт
        ]);

        var (created, modified, deleted, renamed) = _events.CountFileEvents(t0 - 1, t0 + 10_000);
        Assert.Equal(1, created);
        Assert.Equal(1, modified);
        Assert.Equal(1, deleted);
        Assert.Equal(1, renamed);

        var events = _events.GetFileEvents(t0 - 1, t0 + 10_000, 10);
        Assert.Equal(4, events.Count); // новые сверху
        Assert.Equal(ActivityEventKind.FileDeleted, events[0].Kind);
        var renamedEvent = events.Single(e => e.Kind == ActivityEventKind.FileRenamed);
        Assert.Equal(@"C:\p\a.cs", renamedEvent.OldPath);
    }

    [Fact]
    public void BrowserVisits_RoundTrip()
    {
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _events.AppendBatch(
        [
            ActivityEvent.Journal(ActivityEventKind.BrowserVisit, t0,
                """{"url":"https://github.com/x","domain":"github.com","title":"GitHub","browser":"chrome"}"""),
            ActivityEvent.Journal(ActivityEventKind.BrowserVisit, t0 + 1000,
                """{"url":"https://learn.microsoft.com/dotnet","domain":"learn.microsoft.com","title":"Docs","browser":"edge"}"""),
        ]);

        var visits = _events.GetBrowserVisits(t0 - 1, t0 + 10_000, 10);
        Assert.Equal(2, visits.Count);
        Assert.Equal("learn.microsoft.com", visits[0].Domain); // новые сверху
        Assert.Equal("github.com", visits[1].Domain);
        Assert.Equal("chrome", visits[1].Browser);
    }
}
