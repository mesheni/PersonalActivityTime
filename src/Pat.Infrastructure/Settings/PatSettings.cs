namespace Pat.Infrastructure.Settings;

/// <summary>Пользовательские настройки. Хранятся в %LOCALAPPDATA%\PersonalActivityTime\settings.json.</summary>
public sealed class PatSettings
{
    public int IdleThresholdSeconds { get; set; } = 300;

    /// <summary>Имена процессов (без .exe, регистронезависимо), для которых не пишем ни заголовков, ни имён.</summary>
    public List<string> ExcludedProcesses { get; set; } = ["1password", "keepass", "keepassxc", "protonpass"];

    /// <summary>Корневые каталоги наблюдения за файлами (рекурсивно).</summary>
    public List<string> WatchedRoots { get; set; } = [];

    /// <summary>ID браузерного расширения для native messaging (после «Загрузить распакованное»).</summary>
    public string? BrowserExtensionId { get; set; }

    // Локальная LLM (Ollama). Опционально и выключено по умолчанию: только 127.0.0.1.
    public bool OllamaEnabled { get; set; }
    public string OllamaHost { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; set; } = "";

    public bool StartWithWindows { get; set; }
    public bool HasSeenTrayHint { get; set; }

    /// <summary>Сколько дней хранить журнал и сессии; 0 = хранить вечно.</summary>
    public int RetentionDays { get; set; } = 180;

    /// <summary>null = использовать встроенные правила категорий; иначе полная замена списка.</summary>
    public List<Pat.Core.Categorization.CategoryRule>? CategoryRules { get; set; }
}
