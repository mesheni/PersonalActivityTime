using System.IO;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;
using Pat.Infrastructure.Settings;

namespace Pat.App.Services;

/// <summary>
/// Очистка старых данных по настройке RetentionDays (0 = хранить вечно).
/// Вызывается при старте приложения, раз в сутки и по кнопке в настройках.
/// </summary>
public sealed class RetentionService(SettingsStore settingsStore, EventRepository events)
{
    /// <summary>Применить срок хранения. Возвращает описание удалённого или null, если удалять нечего.</summary>
    public string? ApplyRetention()
    {
        var days = settingsStore.Settings.RetentionDays;
        if (days <= 0)
            return null;

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)days * 24 * 60 * 60 * 1000;
        try
        {
            var (deletedEvents, deletedSessions) = events.DeleteOlderThan(cutoff);
            return deletedEvents == 0 && deletedSessions == 0
                ? null
                : $"Удалено по сроку хранения ({days} дн.): событий {deletedEvents}, сессий {deletedSessions}.";
        }
        catch (Exception ex)
        {
            FileLog.Error.Write($"Очистка старых данных не удалась: {ex}");
            return null;
        }
    }

    /// <summary>Ручная очистка с оценкой освобождённого места — для кнопки в настройках.</summary>
    public string PurgeNow()
    {
        var before = DatabaseBytes();
        var removed = ApplyRetention();
        var freedMb = Math.Max(0, before - DatabaseBytes()) / 1024.0 / 1024.0;

        return removed is null
            ? "Нечего удалять: все данные моложе срока хранения (или хранение выключено)."
            : $"{removed}\nОсвобождено ~{freedMb:0.0} МБ.";
    }

    private static long DatabaseBytes()
    {
        var dir = SettingsStore.AppDataDirectory;
        return new[] { "activity.db", "activity.db-wal", "activity.db-shm" }
            .Sum(file =>
            {
                var path = Path.Combine(dir, file);
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            });
    }
}
