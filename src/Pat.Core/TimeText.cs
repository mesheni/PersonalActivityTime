namespace Pat.Core;

public static class TimeText
{
    /// <summary>"3ч 05м", "42м", "18с". Для заголовков и итогов.</summary>
    public static string Humanize(TimeSpan t)
    {
        var total = (long)t.TotalSeconds;
        if (total < 60)
            return $"{total}с";
        if (total < 3600)
            return $"{total / 60}м";
        return $"{total / 3600}ч {total % 3600 / 60:00}м";
    }
}
