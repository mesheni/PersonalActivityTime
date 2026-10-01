using System.IO;
using System.Threading.Channels;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;

namespace Pat.App.Tracking;

public enum TrackingState
{
    Running,
    Paused,
}

/// <summary>
/// Оркестратор: коллекторы → канал → батч-запись в raw_events; периодическая
/// материализация сессий текущего дня. Пауза останавливает коллекторы и пишет
/// маркер TrackingPaused, возобновление — TrackingResumed и рестарт коллекторов.
/// Ошибки записи в БД не глушатся молча: пишутся в error.log.
/// </summary>
public sealed class TrackingService(
    EventRepository events,
    SessionMaterializer materializer,
    ForegroundCollector foreground,
    IdleMonitor idle,
    SessionMonitor session,
    ProcessSnapshotCollector processes,
    SystemMetricsCollector metrics,
    FileActivityCollector files)
{
    private const int ChannelCapacity = 4096;

    private readonly List<IActivityCollector> _collectors = [foreground, idle, session, processes, metrics, files];

    /// <summary>Пока существует, NativeHost браузера не пишет визиты.</summary>
    private static string PausedMarkerPath => Path.Combine(
        Pat.Infrastructure.Settings.SettingsStore.AppDataDirectory, "tracking.paused");

    private Channel<ActivityEvent>? _channel;
    private DropCountingWriter? _countingWriter;
    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private Timer? _materializeTimer;
    private long _reportedDropped;
    private volatile bool _paused;

    public TrackingState State { get; private set; } = TrackingState.Paused;

    public event Action? StateChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _channel = Channel.CreateBounded<ActivityEvent>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        _countingWriter = new DropCountingWriter(_channel, ChannelCapacity);

        // Мусорный маркер после падения в паузе не должен глушить NativeHost
        try { File.Delete(PausedMarkerPath); } catch { /* нет файла — ок */ }

        _writerTask = Task.Run(() => WriterLoopAsync(_channel.Reader, _cts.Token));
        StartCollectors();
        State = TrackingState.Running;
        _paused = false;

        // Первая материализация — после гарантированного флуша журнала (5 с) плюс запас
        _materializeTimer = new Timer(_ => OnPeriodicTick(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60));
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        _materializeTimer?.Dispose();
        _materializeTimer = null;

        foreach (var collector in _collectors)
            collector.Stop();

        if (_cts is { } cts)
        {
            cts.Cancel();
            if (_writerTask is { } task)
            {
                try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            cts.Dispose();
            _cts = null;
        }
        _channel = null;
        _countingWriter = null;
        State = TrackingState.Paused;

        // Финальная материализация: сессии в актуальном виде до следующего запуска
        SafeMaterialize();
    }

    public void TogglePause()
    {
        if (_paused)
        {
            EmitToRunningChannel(ActivityEvent.System(ActivityEventKind.TrackingResumed,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            try { File.Delete(PausedMarkerPath); } catch { /* нет файла — ок */ }
            StartCollectors();
            _paused = false;
            State = TrackingState.Running;
        }
        else
        {
            EmitToRunningChannel(ActivityEvent.System(ActivityEventKind.TrackingPaused,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            foreach (var collector in _collectors)
                collector.Stop();
            FlushNow();
            try { File.WriteAllText(PausedMarkerPath, string.Empty); } catch { /* не критично */ }
            _paused = true;
            State = TrackingState.Paused;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Принудительная материализация сессий (после смены настроек, по кнопке «Обновить»).</summary>
    public void RefreshSessions()
    {
        SafeMaterialize();
        SessionsRefreshed?.Invoke();
    }

    public event Action? SessionsRefreshed;

    private void StartCollectors()
    {
        if (_countingWriter is not { } writer)
            return;
        foreach (var collector in _collectors)
            collector.Start(writer);
    }

    private void EmitToRunningChannel(ActivityEvent ev)
    {
        _countingWriter?.TryWrite(ev);
        FlushNow();
    }

    private void OnPeriodicTick()
    {
        SafeMaterialize();

        // DropOldest теряет события молча — сообщаем о потерях, пока они происходят
        if (_countingWriter is { } writer)
        {
            var dropped = writer.Dropped;
            if (dropped > _reportedDropped)
            {
                FileLog.Error.Write($"Канал переполнен: потеряно ~{dropped - _reportedDropped} событий " +
                                    $"(всего за сессию: {dropped}).");
                _reportedDropped = dropped;
            }
        }
    }

    private async Task WriterLoopAsync(ChannelReader<ActivityEvent> reader, CancellationToken ct)
    {
        var flush = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var batch = new List<ActivityEvent>(256);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await flush.WaitForNextTickAsync(ct).ConfigureAwait(false);
                while (reader.TryRead(out var ev))
                    batch.Add(ev);
                if (batch.Count > 0)
                {
                    try { events.AppendBatch(batch); }
                    catch (Exception ex) { FileLog.Error.Write($"Ошибка записи батча в БД ({batch.Count} событий): {ex}"); }
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }

        // Финальный дренаж перед выходом: падение здесь роняло приложение при выходе
        try
        {
            while (reader.TryRead(out var ev))
                batch.Add(ev);
            if (batch.Count > 0)
                events.AppendBatch(batch);
        }
        catch (Exception ex)
        {
            FileLog.Error.Write($"Не удалось дозаписать {batch.Count} событий при выходе: {ex}");
        }
    }

    private void FlushNow()
    {
        if (_channel is null)
            return;
        var batch = new List<ActivityEvent>();
        while (_channel.Reader.TryRead(out var ev))
            batch.Add(ev);
        if (batch.Count > 0)
        {
            try { events.AppendBatch(batch); }
            catch (Exception ex) { FileLog.Error.Write($"Ошибка записи в БД ({batch.Count} событий): {ex}"); }
        }
    }

    private void SafeMaterialize()
    {
        if (_paused)
            return;
        try
        {
            var (dayStart, _) = LocalDay.Today();
            materializer.Refresh(dayStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            FileLog.Error.Write($"Материализация не удалась: {ex}");
        }
    }

    /// <summary>
    /// Обёртка над writer'ом канала: DropOldest теряет события без уведомления,
    /// поэтому потери оцениваем по заполненности канала на момент записи.
    /// Оценка приблизительная при конкурентной записи, для диагностики этого достаточно.
    /// </summary>
    private sealed class DropCountingWriter(Channel<ActivityEvent> channel, int capacity) : ChannelWriter<ActivityEvent>
    {
        private long _dropped;

        public long Dropped => Interlocked.Read(ref _dropped);

        public override bool TryWrite(ActivityEvent item)
        {
            if (channel.Reader.Count >= capacity)
                Interlocked.Increment(ref _dropped);
            return channel.Writer.TryWrite(item);
        }

        public override bool TryComplete(Exception? error = null) => channel.Writer.TryComplete(error);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            channel.Writer.WaitToWriteAsync(cancellationToken);
    }
}
