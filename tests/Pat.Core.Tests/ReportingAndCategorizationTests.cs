using Pat.Core.Categorization;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;

namespace Pat.Core.Tests;

public sealed class DayReportBuilderTests
{
    private static readonly long DayStart = 1_700_000_000_000;
    private static long At(int minutes) => DayStart + minutes * 60_000L;

    private readonly DayReportBuilder _builder = new();

    [Fact]
    public void CategoriesAndApps_AggregatedIdleAndExcludedSkipped()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(0), At(30), @"C:\rider64.exe", "rider64", "Parser.cs", false, false),
            new(At(30), At(40), null, "Idle", null, true, false),
            new(At(40), At(45), null, "(исключено)", null, false, true),
            new(At(45), At(75), @"C:\chrome.exe", "chrome", "GitHub", false, false),
            new(At(75), At(90), @"C:\rider64.exe", "rider64", "Main.cs", false, false),
        };

        var report = _builder.Build(segments, DayStart, At(24 * 60), CategoryRuleSet.CreateDefault());

        Assert.Equal(TimeSpan.FromMinutes(30 + 30 + 15), report.Active);
        Assert.Equal(TimeSpan.FromMinutes(10), report.Idle);

        Assert.Equal(2, report.Categories.Count);
        Assert.Equal("Программирование", report.Categories[0].Name);
        Assert.Equal(TimeSpan.FromMinutes(45), report.Categories[0].Total);
        Assert.Equal("Браузер", report.Categories[1].Name);

        Assert.Equal(2, report.Apps.Count);
        Assert.Equal("rider64", report.Apps[0].AppName);
        Assert.Equal(TimeSpan.FromMinutes(45), report.Apps[0].Total);
    }

    [Fact]
    public void OvernightSegment_ClippedToDayBounds()
    {
        // сессия вчерашняя, заезжает в наш день на 10 минут
        var segments = new List<ActivitySegment>
        {
            new(DayStart - 30 * 60_000, At(10), @"C:\rider64.exe", "rider64", "Parser.cs", false, false),
        };

        var report = _builder.Build(segments, DayStart, At(24 * 60), CategoryRuleSet.CreateDefault());

        Assert.Single(report.Segments);
        Assert.Equal(DayStart, report.Segments[0].StartUtcMs);
        Assert.Equal(At(10), report.Segments[0].EndUtcMs);
        Assert.Equal(TimeSpan.FromMinutes(10), report.Active);
    }

    [Fact]
    public void SegmentOutsideDay_Dropped()
    {
        var segments = new List<ActivitySegment>
        {
            new(At(24 * 60), At(25 * 60), @"C:\rider64.exe", "rider64", "x", false, false),
        };

        var report = _builder.Build(segments, DayStart, At(24 * 60), CategoryRuleSet.CreateDefault());
        Assert.Empty(report.Segments);
        Assert.Equal(TimeSpan.Zero, report.Active);
    }
}

public sealed class CategorizerTests
{
    [Theory]
    [InlineData("rider64", @"C:\Apps\Rider\rider64.exe", "Программирование")]
    [InlineData("devenv", @"C:\VS\devenv.exe", "Программирование")]
    [InlineData("code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Программирование")]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Браузер")]
    [InlineData("Telegram", @"C:\Telegram\Telegram.exe", "Общение")]
    [InlineData("game", @"D:\Games\SomeGame\game.exe", "Игры")]
    [InlineData("explorer", @"C:\Windows\explorer.exe", "Файлы")]
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", "Другое")]
    public void DefaultRules_ClassifyByAppNameOrPath(string appName, string appPath, string expected)
    {
        var rules = CategoryRuleSet.CreateDefault();
        var (category, _) = rules.Classify(appName, appPath, null);
        Assert.Equal(expected, category);
    }

    [Fact]
    public void LowerPriorityNumberWins()
    {
        var rules = new CategoryRuleSet(
        [
            new CategoryRule("Работа", "#111111", RuleField.AppName, "^rider", Priority: 5),
            new CategoryRule("IDE", "#222222", RuleField.AppName, "^rider", Priority: 0),
        ]);
        var (category, _) = rules.Classify("rider64", null, null);
        Assert.Equal("IDE", category);
    }
}

public sealed class IdeTitleExtractorTests
{
    [Theory]
    [InlineData("Parser.cs — WaferAnalyzer — JetBrains Rider", "WaferAnalyzer", "Parser.cs")]
    [InlineData("MainWindow.xaml - WaferAnalyzer - Visual Studio Code", "WaferAnalyzer", "MainWindow.xaml")]
    public void IdeTitles_ExtractProjectAndFile(string title, string expectedProject, string expectedFile)
    {
        var ctx = Pat.Core.Context.IdeTitleExtractor.Extract(title);
        Assert.NotNull(ctx);
        Assert.Equal(expectedProject, ctx.Project);
        Assert.Equal(expectedFile, ctx.File);
    }

    [Theory]
    [InlineData("WaferAnalyzer — JetBrains Rider")]         // нет файла
    [InlineData("Inbox (3) — Gmail")]                        // один разделитель
    [InlineData(null)]
    [InlineData("")]
    public void NonIdeTitles_ReturnNull(string? title)
    {
        Assert.Null(Pat.Core.Context.IdeTitleExtractor.Extract(title));
    }
}
