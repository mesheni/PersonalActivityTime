using System.Text.RegularExpressions;

namespace Pat.Core.Categorization;

/// <summary>
/// Компилированный набор правил. Побеждает правило с минимальным Priority
/// (при равенстве — раньше в списке). Без совпадений — «Другое».
/// </summary>
public sealed class CategoryRuleSet
{
    public const string FallbackCategory = "Другое";
    public const string FallbackColorHex = "#94A3B8";

    private readonly record struct CompiledRule(Regex Regex, RuleField Field, string Category, string ColorHex, int Priority);

    private readonly List<CompiledRule> _rules;

    public CategoryRuleSet(IEnumerable<CategoryRule>? rules)
    {
        _rules = (rules ?? [])
            .Select(r => new CompiledRule(
                new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                r.Field, r.Category, r.ColorHex, r.Priority))
            .OrderBy(r => r.Priority)
            .ToList();
    }

    public (string Category, string ColorHex) Classify(string? appName, string? appPath, string? windowTitle)
    {
        foreach (var rule in _rules)
        {
            var value = rule.Field switch
            {
                RuleField.AppName => appName,
                RuleField.AppPath => appPath,
                RuleField.WindowTitle => windowTitle,
                _ => null,
            };
            if (value is not null && rule.Regex.IsMatch(value))
                return (rule.Category, rule.ColorHex);
        }
        return (FallbackCategory, FallbackColorHex);
    }

    /// <summary>Разумный стартовый набор: расширяется в settings.json.</summary>
    public static CategoryRuleSet CreateDefault() => new(DefaultRules);

    public static IReadOnlyList<CategoryRule> DefaultRules { get; } =
    [
        new("Программирование", "#3B82F6", RuleField.AppName,
            @"^(rider64|idea64|pycharm64|webstorm64|clion64|devenv|code|code - insiders|cursor|sublime_text|notepad\+\+|windowsterminal|wt|wezterm|alacritty|windowsterminal|neovide|vim)$", Priority: 10),
        new("Браузер", "#8B5CF6", RuleField.AppName,
            @"^(chrome|msedge|firefox|opera|vivaldi|brave|yandexbrowser|arc|chromium)$", Priority: 20),
        new("Общение", "#10B981", RuleField.AppName,
            @"^(telegram|discord|slack|teams|teams\d?|whatsapp|signal|skype|viber|zoom)$", Priority: 30),
        new("Игры", "#EF4444", RuleField.AppPath,
            @"\\(games|steamapps|epic games|riot games)\\", Priority: 40),
        new("Игры", "#EF4444", RuleField.AppName,
            @"^(steam|epicgameslauncher|riotclientservices|battle\.net|ea app|xboxgames)$", Priority: 41),
        new("Файлы", "#F59E0B", RuleField.AppName, @"^(explorer|totalcmd|totalcmd64|doublecmd)$", Priority: 50),
        new("Медиа", "#EC4899", RuleField.AppName, @"^(spotify|vlc|potplayer|mpc-hc64|winamp|foobar2000|youtube music|itunes)$", Priority: 60),
    ];
}
