using Pat.Core.Categorization;
using Pat.Core.Narrative;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;

namespace Pat.Core.Tests;

public sealed class NarrativeBuilderTests
{
    private static readonly long DayStart = 1_700_000_000_000;
    private static long At(int minutes) => DayStart + minutes * 60_000L;

    private readonly NarrativeBuilder _builder = new();
    private readonly CategoryRuleSet _rules = CategoryRuleSet.CreateDefault();

    private DayReport ReportOf(List<ActivitySegment> segments) =>
        new DayReportBuilder().Build(segments, DayStart, At(24 * 60), _rules);

    [Fact]
    public void WorkWithBrowserLookup_MergesIntoOneBlockTitledByProject()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(14), @"C:\rider64.exe", "rider64", "Parser.cs — WaferAnalyzer — Rider", false, false),
            new(At(14), At(27), @"C:\chrome.exe", "chrome", "learn.microsoft.com — docs", false, false),
            new(At(27), At(51), @"C:\rider64.exe", "rider64", "Parser.cs — WaferAnalyzer — Rider", false, false),
        };
        var browser = new List<BrowserFact>
        {
            new(At(20), "learn.microsoft.com"),
            new(At(22), "learn.microsoft.com"),
            new(At(24), "github.com"),
        };

        var narrative = _builder.Build(ReportOf(segments), browser, [], _rules);

        var block = Assert.Single(narrative.Blocks);
        Assert.Equal("Работа над WaferAnalyzer", block.Title);
        Assert.Equal(TimeSpan.FromMinutes(51), block.Duration);
        // Браузерные домены внутри окна отрезка попали в детали
        Assert.Contains(block.Details, d => d.Contains("learn.microsoft.com"));
        // Программирование — доминирующая категория в деталях
        Assert.Contains(block.Details, d => d.StartsWith("Программирование"));
        Assert.Equal(TimeSpan.FromMinutes(51), narrative.LongestStreak);
        Assert.Equal(2, narrative.ContextSwitches); // rider→chrome→rider
    }

    [Fact]
    public void LongGap_SplitsIntoTwoBlocks()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(9 * 60), At(10 * 60), @"C:\rider64.exe", "rider64", "code", false, false),
            new(At(12 * 60), At(13 * 60), @"C:\rider64.exe", "rider64", "code", false, false),
        };

        var narrative = _builder.Build(ReportOf(segments), [], [], _rules);

        Assert.Equal(2, narrative.Blocks.Count);
    }

    [Fact]
    public void QuickMessengerBreak_WithinMergeGap_StaysInBlock()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(30), @"C:\rider64.exe", "rider64", "code", false, false),
            new(At(30), At(32), @"C:\telegram.exe", "telegram", "chat", false, false),
            new(At(33), At(60), @"C:\rider64.exe", "rider64", "code", false, false),
        };

        var narrative = _builder.Build(ReportOf(segments), [], [], _rules);

        var block = Assert.Single(narrative.Blocks);
        Assert.Equal(TimeSpan.FromMinutes(60), block.Duration);
        Assert.Equal("Программирование", block.Title); // IDE-проекта нет, доминирует категория
        Assert.Contains(block.Details, d => d.Contains("Общение"));
    }

    [Fact]
    public void BrowserOnlyDay_TitledByCategoryWithDomains()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(60), At(90), @"C:\chrome.exe", "chrome", "web", false, false),
        };
        var browser = new List<BrowserFact>
        {
            new(At(61), "github.com"), new(At(65), "github.com"), new(At(70), "habr.com"),
        };

        var narrative = _builder.Build(ReportOf(segments), browser, [], _rules);

        var block = Assert.Single(narrative.Blocks);
        Assert.Equal("Браузер", block.Title);
        Assert.Contains(block.Details, d => d.Contains("github.com ×2"));
    }

    [Fact]
    public void FileFacts_SummarizedInBlockDetails()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(20), @"C:\rider64.exe", "rider64", "Parser.cs — Wafer — Rider", false, false),
        };
        var files = new List<FileFact>
        {
            new(At(5), Pat.Core.Events.ActivityEventKind.FileCreated, @"C:\p\new.cs"),
            new(At(8), Pat.Core.Events.ActivityEventKind.FileModified, @"C:\p\a.cs"),
            new(At(10), Pat.Core.Events.ActivityEventKind.FileModified, @"C:\p\b.cs"),
            new(At(15), Pat.Core.Events.ActivityEventKind.FileDeleted, @"C:\p\old.cs"), // удаления в дайджест отрезка не входят
        };

        var narrative = _builder.Build(ReportOf(segments), [], files, _rules);

        var block = Assert.Single(narrative.Blocks);
        Assert.Contains(block.Details, d => d.Contains("создано 1") && d.Contains("изменено 2"));
    }

    [Fact]
    public void IdleAndExcluded_AreNotNarrativeBlocks()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(30), @"C:\rider64.exe", "rider64", "code", false, false),
            new(At(30), At(60), null, "Idle", null, true, false),
            new(At(60), At(70), null, "(исключено)", null, false, true),
        };

        var narrative = _builder.Build(ReportOf(segments), [], [], _rules);

        var block = Assert.Single(narrative.Blocks);
        Assert.Equal("Программирование", block.Title);
        Assert.Equal(TimeSpan.FromMinutes(30), block.Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), narrative.Active);
        Assert.Equal(TimeSpan.FromMinutes(30), narrative.Idle);
    }

    [Fact]
    public void EmptyDay_EmptyNarrative()
    {
        var narrative = _builder.Build(ReportOf([]), [], [], _rules);

        Assert.Empty(narrative.Blocks);
        Assert.Equal(TimeSpan.Zero, narrative.LongestStreak);
        Assert.Equal(0, narrative.ContextSwitches);
    }

    [Fact]
    public void Digest_ContainsKeyFactsAndBounded()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(30), @"C:\rider64.exe", "rider64", "Parser.cs — Wafer — Rider", false, false),
            new(At(30), At(40), null, "Idle", null, true, false),
            new(At(40), At(60), @"C:\chrome.exe", "chrome", "GitHub", false, false),
        };
        var report = ReportOf(segments);
        var narrative = _builder.Build(report, [], [], _rules);

        var digest = NarrativeDigest.Build(report, narrative);

        Assert.Contains("Активное время: 50м", digest);
        Assert.Contains("простой: 10м", digest);
        Assert.Contains("rider64", digest);
        Assert.Contains("chrome", digest);
        Assert.Contains("—", digest); // хроника
        Assert.True(digest.Length < 8000, "дайджест должен оставаться компактным");
    }
}
