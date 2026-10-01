using System.IO;
using System.Text.Json;

namespace Pat.Infrastructure.Settings;

/// <summary>Загрузка/сохранение settings.json. Один экземпляр на процесс; после Save текущий объект остаётся актуальным.</summary>
public sealed class SettingsStore
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalActivityTime");

    private static string SettingsPath { get; } = Path.Combine(AppDataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PatSettings Settings { get; private set; } = new();

    public PatSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<PatSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded is not null)
                    Settings = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл настроек не должен ломать приложение: живём с дефолтами.
        }
        return Settings;
    }

    public void Save(PatSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        Settings = settings;
    }
}
