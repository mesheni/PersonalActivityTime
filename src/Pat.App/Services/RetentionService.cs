using Pat.Infrastructure;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;
using Pat.Infrastructure.Settings;

namespace Pat.App.Services;

public sealed class RetentionService(SettingsStore settingsStore, EventRepository events)
{
    public OperationResult ApplyRetention()
    {
        var days = settingsStore.Settings.RetentionDays;
        if (days <= 0) return new(OperationStatus.NoChanges, "Данные хранятся бессрочно; автоматическая очистка отключена.");
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)days * 86_400_000;
        return PurgeBefore(cutoff);
    }

    public OperationResult PurgeBefore(long cutoffUtcMs)
    {
        try
        {
            var (deletedEvents, deletedSessions) = events.DeleteOlderThan(cutoffUtcMs);
            return deletedEvents == 0 && deletedSessions == 0
                ? new(OperationStatus.NoChanges, "Старых данных для удаления нет.")
                : new(OperationStatus.Success, $"Удалено событий: {deletedEvents}, сессий: {deletedSessions}. Свободные страницы базы будут использованы повторно.");
        }
        catch (Exception ex)
        {
            FileLog.Error.Write($"Очистка старых данных не удалась: {ex}");
            return new(OperationStatus.Error, "Не удалось удалить данные: " + ex.Message);
        }
    }
}
