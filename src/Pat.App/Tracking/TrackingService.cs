using System.IO;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;
using Pat.Infrastructure.Settings;

namespace Pat.App.Tracking;

public enum TrackingState { Running, Paused }

/// <summary>Serializes lifecycle transitions; only BufferedEventWriter consumes events.</summary>
public sealed class TrackingService(
    EventRepository events, SessionMaterializer materializer,
    ForegroundCollector foreground, IdleMonitor idle, SessionMonitor session,
    ProcessSnapshotCollector processes, SystemMetricsCollector metrics, FileActivityCollector files, SettingsStore settingsStore)
{
    private readonly List<IActivityCollector> _collectors = [session, idle, foreground, processes, metrics, files];
    private readonly SemaphoreSlim _transition = new(1, 1);
    private BufferedEventWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _periodic;
    private volatile bool _stopping;
    private string PausedMarkerPath => Path.Combine(settingsStore.DirectoryPath, "tracking.paused");
    public TrackingState State { get; private set; } = TrackingState.Paused;
    public string? StorageError { get; private set; }
    public bool IsStarted => _writer is not null;
    public event Action? StateChanged;
    public event Action? SessionsRefreshed;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Start()
    {
        if (_writer is not null) return;
        _stopping = false;
        File.Delete(PausedMarkerPath);
        _writer = new BufferedEventWriter(events, Path.Combine(settingsStore.DirectoryPath, "pending-events"));
        _writer.HealthChanged += ex =>
        {
            var error = ex?.Message;
            if (StorageError == error) return;
            StorageError = error;
            StateChanged?.Invoke();
            if (_writer?.FatalError is not null) _ = PauseAfterDiskFailureAsync();
        };
        Emit(ActivityEventKind.TrackingStarted);
        foreach (var collector in _collectors) collector.Start(_writer.Writer);
        State = TrackingState.Running;
        _cts = new CancellationTokenSource();
        _periodic = PeriodicAsync(_cts.Token);
        StateChanged?.Invoke();
        if (_writer.FatalError is not null) _ = PauseAfterDiskFailureAsync();
    }

    private async Task PauseAfterDiskFailureAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            foreach (var collector in _collectors) collector.Stop();
            State = TrackingState.Paused;
            try { File.WriteAllText(PausedMarkerPath, ""); } catch (IOException) { }
        }
        catch (Exception ex) { FileLog.Error.Write(ex); }
        finally { _transition.Release(); StateChanged?.Invoke(); }
    }

    private void Emit(ActivityEventKind kind)
    {
        if (_writer?.Writer.TryWrite(ActivityEvent.System(kind, Now)) != true)
            throw new IOException("Журнал событий недоступен. Перезапустите трекер после устранения ошибки диска.");
    }

    public async Task TogglePauseAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is null || _stopping) return;
            if (State == TrackingState.Paused)
            {
                Emit(ActivityEventKind.TrackingResumed);
                foreach (var collector in _collectors) collector.Start(_writer.Writer);
                File.Delete(PausedMarkerPath);
                State = TrackingState.Running;
            }
            else
            {
                // Block the independent browser producer before stopping local producers.
                File.WriteAllText(PausedMarkerPath, "");
                foreach (var collector in _collectors) collector.Stop();
                Emit(ActivityEventKind.TrackingPaused);
                State = TrackingState.Paused;
            }
            await FlushAndMaterializeAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); StateChanged?.Invoke(); }
    }

    public async Task RefreshSessionsAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try { if (!_stopping) await FlushAndMaterializeAsync().ConfigureAwait(false); }
        finally { _transition.Release(); }
    }

    private async Task FlushAndMaterializeAsync()
    {
        if (_writer is null) return;
        await _writer.FlushAsync().ConfigureAwait(false);
        var (start, _) = LocalDay.Today();
        await Task.Run(() => materializer.Refresh(start, Now)).ConfigureAwait(false);
        SessionsRefreshed?.Invoke();
    }

    private async Task PeriodicAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _transition.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_stopping) return;
                    Emit(ActivityEventKind.TrackingHeartbeat);
                    await FlushAndMaterializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { FileLog.Error.Write(ex); }
                finally { _transition.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_periodic is not null) await _periodic.ConfigureAwait(false);
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is null) return;
            foreach (var collector in _collectors) collector.Stop();
            try { Emit(ActivityEventKind.TrackingStopped); await FlushAndMaterializeAsync().ConfigureAwait(false); }
            catch (Exception ex) { FileLog.Error.Write(ex); } // durable spool is replayed next launch
            try { await _writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { FileLog.Error.Write(ex); }
            _writer = null;
            _cts?.Dispose();
            _cts = null;
            State = TrackingState.Paused;
        }
        finally { _transition.Release(); }
    }
}
