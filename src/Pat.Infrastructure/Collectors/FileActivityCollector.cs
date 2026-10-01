using System.Text.Json;
using System.Threading.Channels;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Logging;
using Pat.Infrastructure.Settings;

namespace Pat.Infrastructure.Collectors;

/// <summary>
/// Наблюдение за выбранными в настройках корнями (FileSystemWatcher, IncludeSubdirectories).
/// События дебаунсятся через FileEventBuffer и раз в секунду уходят в журнал.
///
/// Шум отфильтровывается: служебные каталоги (bin, obj, node_modules, .git, ...),
/// временные файлы (.tmp, .swp, ~$, ...), временные каталоги и собственная база
/// приложения (PersonalActivityTime). NotifyFilter без Attributes/Security/CreationTime —
/// иначе Changed приходит на каждое касание метаданных.
///
/// Переполнение внутреннего буфера FSW не останавливает наблюдение: после Error
/// watcher пересоздаётся для того же корня (с записью в лог), поэтому потеря
/// событий ограничивается моментом сбоя.
/// </summary>
public sealed class FileActivityCollector(SettingsStore settingsStore) : IActivityCollector
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".svn", ".hg", ".idea", ".vs", ".vscode",
        "packages", "target", "vendor", "__pycache__", ".venv", "venv", ".gradle",
        ".dart_tool", "pods", ".terraform", ".next", ".nuxt",
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".swp", ".swo", ".lnk",
    };

    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;
    private readonly List<(string Root, FileSystemWatcher Watcher)> _watchers = [];
    private readonly FileEventBuffer _buffer = new();
    private Timer? _flushTimer;

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record FilePayload(string Path, string? OldPath);

    /// <summary>Применить новые корни без пересоздания коллектора (вызывается при сохранении настроек).</summary>
    public void Reconfigure()
    {
        bool running;
        lock (_gate)
        {
            running = _writer is not null;
        }
        if (running)
        {
            StopWatchers();
            StartWatchers();
        }
    }

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            _writer = writer;
        }
        StartWatchers();
        _flushTimer = new Timer(_ => FlushBuffer(), null, FlushInterval, FlushInterval);
    }

    void IActivityCollector.Stop()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        StopWatchers();
        lock (_gate)
        {
            _writer = null;
        }
    }

    private void StartWatchers()
    {
        var roots = settingsStore.Settings.WatchedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
            AttachWatcher(root);
    }

    private void AttachWatcher(string root)
    {
        if (!Directory.Exists(root))
            return;

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        watcher.Created += (_, e) => OnFsEvent(ActivityEventKind.FileCreated, e.FullPath, null);
        watcher.Changed += (_, e) => OnFsEvent(ActivityEventKind.FileModified, e.FullPath, null);
        watcher.Deleted += (_, e) => OnFsEvent(ActivityEventKind.FileDeleted, e.FullPath, null);
        watcher.Renamed += (_, e) => OnFsEvent(ActivityEventKind.FileRenamed, e.FullPath, e.OldFullPath);
        watcher.Error += (_, e) => OnWatcherError(root, e.GetException());

        lock (_gate)
        {
            // Пока готовили watcher, коллектор могли остановить — не оставляем хвост
            if (_writer is null)
            {
                watcher.Dispose();
                return;
            }
            _watchers.Add((root, watcher));
        }
        watcher.EnableRaisingEvents = true;
    }

    private void OnWatcherError(string root, Exception exception)
    {
        FileLog.Error.Write($"Ошибка FileSystemWatcher на «{root}»: {exception.Message} — перезапускаю наблюдение.");
        DetachWatcher(root);

        // Переполненный буфер не должен глушить наблюдение до перезапуска приложения:
        // пересоздаём watcher для того же корня, если коллектор ещё работает
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RestartDelay).ConfigureAwait(false);
                bool running;
                lock (_gate)
                {
                    running = _writer is not null;
                }
                if (!running)
                    return;
                DetachWatcher(root); // на случай гонки с параллельным восстановлением
                AttachWatcher(root);
                FileLog.Error.Write($"Наблюдение «{root}» перезапущено.");
            }
            catch (Exception retryException)
            {
                FileLog.Error.Write($"Не удалось перезапустить наблюдение «{root}»: {retryException.Message}");
            }
        });
    }

    private void DetachWatcher(string root)
    {
        FileSystemWatcher[] found;
        lock (_gate)
        {
            found = [.. _watchers
                .Where(w => string.Equals(w.Root, root, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Watcher)];
            _watchers.RemoveAll(w => string.Equals(w.Root, root, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var watcher in found)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch { /* уже уничтожен */ }
        }
    }

    private void StopWatchers()
    {
        FileSystemWatcher[] watchers;
        lock (_gate)
        {
            watchers = [.. _watchers.Select(w => w.Watcher)];
            _watchers.Clear();
        }
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void OnFsEvent(ActivityEventKind kind, string path, string? oldPath)
    {
        if (ShouldIgnore(path) || (oldPath is not null && ShouldIgnore(oldPath)))
            return;

        _buffer.Add(new BufferedFileEvent(
            kind, path, oldPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private void FlushBuffer()
    {
        ChannelWriter<ActivityEvent>? writer;
        lock (_gate)
        {
            writer = _writer;
        }
        if (writer is null)
            return;

        foreach (var ev in _buffer.Flush())
        {
            var payload = JsonSerializer.Serialize(
                new FilePayload(ev.Path, ev.OldPath), PayloadJson);
            writer.TryWrite(ActivityEvent.Journal(ev.Kind, ev.TimestampUtcMs, payload));
        }
    }

    public static bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        // Собственные файлы трекера не наблюдаем, иначе база самоинкрементируется
        if (path.Contains("PersonalActivityTime", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = System.IO.Path.GetFileName(path);

        // Временные файлы Office/LibreOffice/редакторов: ~$, ~$, ...~, ~name
        if (fileName.Contains("~$") || fileName.StartsWith('~') || fileName.EndsWith('~'))
            return true;
        if (IgnoredExtensions.Contains(System.IO.Path.GetExtension(fileName)))
            return true;

        // Служебные каталоги: любой сегмент пути
        var directory = System.IO.Path.GetDirectoryName(path) ?? "";
        foreach (var segment in directory.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]))
        {
            if (segment.Length == 0)
                continue;
            if (IgnoredDirNames.Contains(segment))
                return true;
            if (segment.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("tmp", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
