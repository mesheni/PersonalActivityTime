using Pat.Core.Events;

namespace Pat.Core.Narrative;

/// <summary>Факт браузерного визита для нарратива (домен уже извлечён).</summary>
public sealed record BrowserFact(long TsUtcMs, string Domain);

/// <summary>Факт файловой активности для нарратива.</summary>
public sealed record FileFact(long TsUtcMs, ActivityEventKind Kind, string Path);

public sealed record NarrativeOptions
{
    /// <summary>Активность с разрывом не больше этого склеивается в один отрезок работы:
    /// быстрый alt-tab в мессенджер или в браузер за документацией не рвёт контекст.</summary>
    public int MergeGapMs { get; init; } = 4 * 60_000;

    public static NarrativeOptions Default { get; } = new();
}

/// <summary>Отрезок работы: цепочка активности с общим контекстом.</summary>
public sealed record NarrativeBlock(
    long StartUtcMs,
    long EndUtcMs,
    string Title,
    string CategoryName,
    string ColorHex,
    IReadOnlyList<string> Details)
{
    public TimeSpan Duration => TimeSpan.FromMilliseconds(EndUtcMs - StartUtcMs);
}

public sealed record DayNarrative(
    TimeSpan Active,
    TimeSpan Idle,
    TimeSpan LongestStreak,
    int ContextSwitches,
    IReadOnlyList<NarrativeBlock> Blocks);
