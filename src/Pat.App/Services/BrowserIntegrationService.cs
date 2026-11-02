using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pat.App.Services;

/// <summary>
/// Установка native messaging хоста: манифест JSON + ключи реестра HKCU
/// (Chrome и Edge). Без админ-прав. Расширение грузится unpacked, его ID
/// пользователь вставляет в настройках.
/// </summary>
public sealed partial class BrowserIntegrationService
{
    public const string HostName = "com.personalactivitytime.host";

    private static string ManifestDirectory =>
        Path.Combine(Infrastructure.Settings.SettingsStore.AppDataDirectory, "browser-integration");

    private static string ManifestPath => Path.Combine(ManifestDirectory, $"{HostName}.json");

    private const string ChromeRegistryKey =
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;

    private const string EdgeRegistryKey =
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName;

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ExtensionIdPattern();

    public bool IsInstalled =>
        Registry.CurrentUser.OpenSubKey(ChromeRegistryKey) is not null
        || Registry.CurrentUser.OpenSubKey(EdgeRegistryKey) is not null;

    /// <summary>Записать манифест и ключи реестра. Возвращает сообщение для пользователя.</summary>
    public (bool Ok, string Message) Install(string extensionId)
    {
        extensionId = extensionId.Trim();
        if (!ExtensionIdPattern().IsMatch(extensionId))
            return (false, "ID расширения должен быть строкой из 32 латинских букв (a–p). Скопируйте его на странице chrome://extensions.");

        var hostExe = Path.Combine(
            AppContext.BaseDirectory, "nativehost", "Pat.NativeHost.exe");
        if (!File.Exists(hostExe))
            return (false, $"Не найден {hostExe}. Соберите решение целиком: dotnet build.");

        var manifest = new
        {
            name = HostName,
            description = "Personal Activity Time — локальная интеграция с браузером",
            path = hostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        };

        try
        {
            Directory.CreateDirectory(ManifestDirectory);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            using (var chrome = Registry.CurrentUser.CreateSubKey(ChromeRegistryKey))
            {
                chrome.SetValue(null, ManifestPath);
            }
            using (var edge = Registry.CurrentUser.CreateSubKey(EdgeRegistryKey))
            {
                edge.SetValue(null, ManifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Не удалось записать манифест: {ex.Message}");
        }

        return (true, $"Готово. Перезапустите браузер — визиты появятся в карточке «Браузер».\nМанифест: {ManifestPath}");
    }

    public void Uninstall()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ChromeRegistryKey, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(EdgeRegistryKey, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(ManifestDirectory, recursive: true); } catch { }
    }
}
