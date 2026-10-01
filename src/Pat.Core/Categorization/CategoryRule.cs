namespace Pat.Core.Categorization;

public enum RuleField
{
    AppName,
    AppPath,
    WindowTitle,
}

/// <param name="Pattern">Регулярное выражение, match по полю Field, регистронезависимо.</param>
public sealed record CategoryRule(
    string Category,
    string ColorHex,
    RuleField Field,
    string Pattern,
    int Priority = 0);
