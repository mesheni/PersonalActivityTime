using System.Collections.Concurrent;
using Pat.Core.Events;

namespace Pat.Infrastructure.Collectors;

/// <summary>Событие наблюдения за файлами, ожидающее дебаунс-флеша.</summary>
public sealed record BufferedFileEvent(
    ActivityEventKind Kind,
    string Path,
    string? OldPath,
    long TimestampUtcMs);

/// <summary>
/// Дебаунс событий FileSystemWatcher: на одно сохранение файла FSW шлёт 3–5 событий Changed.
/// Буфер копит события по пути, «последний сильный wins» (Deleted &gt; Renamed &gt; Created &gt; Modified),
/// Flush отдаёт по одному событию на путь. Чистый класс — тестируется без файловой системы.
/// </summary>
public sealed class FileEventBuffer
{
    private static int Rank(ActivityEventKind kind) => kind switch
    {
        ActivityEventKind.FileModified => 0,
        ActivityEventKind.FileCreated => 1,
        ActivityEventKind.FileRenamed => 2,
        ActivityEventKind.FileDeleted => 3,
        _ => 0,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, BufferedFileEvent> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Добавить событие; события того же пути сливаются, приоритет — более «сильный» вид.</summary>
    public void Add(BufferedFileEvent ev)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(ev.Path, out var existing)
                && Rank(existing.Kind) > Rank(ev.Kind))
            {
                return; // уже есть более сильное событие по этому пути
            }
            _pending[ev.Path] = ev;
        }
    }

    /// <summary>Слить накопленное: по одному событию на путь, буфер очищается.</summary>
    public List<BufferedFileEvent> Flush()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];

            var result = _pending.Values.OrderBy(e => e.TimestampUtcMs).ToList();
            _pending.Clear();
            return result;
        }
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }
}
