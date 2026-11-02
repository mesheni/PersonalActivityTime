using System.IO;
using System.Text.Json;

namespace Pat.Infrastructure.Settings;

/// <summary>Загрузка/сохранение settings.json. Один экземпляр на процесс; после Save текущий объект остаётся актуальным.</summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _directory;
    public string DirectoryPath => _directory;
    public SettingsStore(string? directory = null) => _directory = directory ?? AppDataDirectory;
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalActivityTime");

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private PatSettings _settings = new();
    public string? LastError { get; private set; }
    public bool HasUsableSettings { get; private set; } = true;
    public PatSettings Settings
    {
        get { lock (_gate) return Copy(_settings); }
    }

    private static PatSettings Copy(PatSettings s) => new()
    {
        IdleThresholdSeconds = s.IdleThresholdSeconds, ExcludedProcesses = [.. s.ExcludedProcesses],
        WatchedRoots = [.. s.WatchedRoots], BrowserExtensionId = s.BrowserExtensionId,
        OllamaEnabled = s.OllamaEnabled, OllamaHost = s.OllamaHost, OllamaModel = s.OllamaModel,
        StartWithWindows = s.StartWithWindows, RetentionDays = s.RetentionDays, HasSeenTrayHint = s.HasSeenTrayHint,
        CategoryRules = s.CategoryRules is null ? null : [.. s.CategoryRules],
    };

    private static PatSettings Validate(PatSettings s)
    {
        s.ExcludedProcesses ??= new PatSettings().ExcludedProcesses;
        s.WatchedRoots ??= [];
        s.ExcludedProcesses = s.ExcludedProcesses.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.WatchedRoots = s.WatchedRoots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (s.IdleThresholdSeconds < 30 || s.IdleThresholdSeconds > 86400 || s.RetentionDays < 0)
            throw new ArgumentException("Некорректный порог простоя или срок хранения.");
        s.OllamaHost = Ollama.OllamaClient.ValidateHost(s.OllamaHost).AbsoluteUri.TrimEnd('/');
        s.OllamaModel ??= "";
        if (s.CategoryRules is { } rules)
        {
            foreach (var rule in rules)
            {
                if (rule is null || string.IsNullOrWhiteSpace(rule.Category)
                    || !System.Text.RegularExpressions.Regex.IsMatch(rule.ColorHex ?? "", "^#[0-9a-fA-F]{6}$"))
                    throw new ArgumentException("Некорректное правило категории или цвет.");
            }
            _ = new Pat.Core.Categorization.CategoryRuleSet(rules);
        }
        return s;
    }

    public PatSettings Load()
    {
        lock (_gate)
        {
            LastError = null;
            HasUsableSettings = true;
            foreach (var path in new[] { SettingsPath, SettingsPath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    _settings = Validate(JsonSerializer.Deserialize<PatSettings>(File.ReadAllText(path), JsonOptions)
                        ?? throw new JsonException("Пустые настройки."));
                    HasUsableSettings = true;
                    return Copy(_settings);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
                {
                    LastError = "Не удалось прочитать настройки: " + ex.Message + " Используется резервная копия или значения по умолчанию.";
                    HasUsableSettings = false;
                    Logging.FileLog.Error.Write(LastError);
                }
            }
            return Copy(_settings);
        }
    }

    public void Save(PatSettings settings)
    {
        lock (_gate)
        {
            var validated = Validate(JsonSerializer.Deserialize<PatSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!);
            Directory.CreateDirectory(_directory);
            var temp = SettingsPath + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, validated, JsonOptions);
                    stream.Flush(true);
                }
                if (File.Exists(SettingsPath) && LastError is null) File.Replace(temp, SettingsPath, SettingsPath + ".bak");
                else if (File.Exists(SettingsPath)) File.Move(temp, SettingsPath, true); // keep the valid recovery backup
                else File.Move(temp, SettingsPath);
                _settings = validated;
                LastError = null;
                HasUsableSettings = true;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public OperationResult TrySave(PatSettings settings)
    {
        try { Save(settings); return new(OperationStatus.Success, "Настройки сохранены."); }
        catch (Exception ex) { return new(OperationStatus.Error, "Настройки не сохранены: " + ex.Message); }
    }
}
