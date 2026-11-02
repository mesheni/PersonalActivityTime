namespace Pat.Core.Events;

public enum ActivityEventKind
{
    /// <summary>Окно/приложение стало активным (передний план).</summary>
    ForegroundChanged = 1,

    /// <summary>Пользователь неактивен дольше порога. Метка времени = момент последнего ввода.</summary>
    IdleBegan = 2,

    /// <summary>Пользователь вернулся. Метка времени = момент нового ввода.</summary>
    IdleEnded = 3,

    SessionLocked = 4,
    SessionUnlocked = 5,
    SystemSuspend = 6,
    SystemResume = 7,

    /// <summary>Пауза трекинга по команде пользователя.</summary>
    TrackingPaused = 8,
    TrackingResumed = 9,

    // Журнальные события: в сессиях не участвуют, движок их пропускает.

    ProcessStarted = 10,
    ProcessStopped = 11,

    /// <summary>Системный сэмпл CPU/RAM (payload = JSON).</summary>
    SystemSample = 12,

    // Журнальные события наблюдения за файлами (payload = JSON {path, oldPath?}).

    FileCreated = 13,
    FileModified = 14,
    FileDeleted = 15,
    FileRenamed = 16,

    /// <summary>Активная вкладка браузера от расширения (payload = JSON {url, domain, title, browser}).</summary>
    BrowserVisit = 17,

    TrackingStarted = 18,
    TrackingStopped = 19,
    TrackingHeartbeat = 20,
}
