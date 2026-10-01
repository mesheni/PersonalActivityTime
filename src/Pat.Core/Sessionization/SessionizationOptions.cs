namespace Pat.Core.Sessionization;

public sealed record SessionizationOptions
{
    /// <summary>
    /// Подряд идущие активные сегменты одного приложения с одинаковым (нормализованным)
    /// заголовком и промежутком не длиннее этого склеиваются (дрожание хука, дубликаты).
    /// </summary>
    public int MergeGapMs { get; init; } = 5000;

    public static SessionizationOptions Default { get; } = new();
}
