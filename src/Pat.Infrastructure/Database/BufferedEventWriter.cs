using System.Text.Json;
using System.Threading.Channels;
using Pat.Core.Events;

namespace Pat.Infrastructure.Database;

/// <summary>Single reader, acknowledged flush, recoverable disk spool and idempotent commits.</summary>
public sealed class BufferedEventWriter : IAsyncDisposable
{
    private sealed record Request(ActivityEvent? Event, TaskCompletionSource? Barrier);
    private readonly Channel<Request> _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(8192)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly EventRepository _events;
    private readonly string _spool;
    private readonly Task _task;
    private readonly Adapter _adapter;
    public ChannelWriter<ActivityEvent> Writer => _adapter;
    public Exception? FatalError { get; private set; }
    public event Action<Exception?>? HealthChanged;

    public BufferedEventWriter(EventRepository events, string spoolDirectory)
    {
        _events = events;
        _spool = spoolDirectory;
        Directory.CreateDirectory(_spool);
        foreach (var temp in Directory.EnumerateFiles(_spool, "*.json.tmp"))
        {
            try
            {
                _ = JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(temp))
                    ?? throw new JsonException("Empty pending batch.");
                File.Move(temp, temp[..^4]);
            }
            catch (JsonException ex)
            {
                Logging.FileLog.Error.Write(ex);
                File.Move(temp, temp + ".corrupt"); // preserve evidence; never pass partial JSON to SQLite
            }
        }
        _adapter = new Adapter(this);
        _task = Task.Run(RunAsync);
    }

    public async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(null, completion)).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _task.ConfigureAwait(false);
    }

    private void Save(List<ActivityEvent> batch)
    {
        if (batch.Count == 0) return;
        var path = Path.Combine(_spool, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, batch);
            stream.Flush(true);
        }
        File.Move(temp, path);
        batch.Clear(); // only after the batch is durable
    }

    private Exception? Replay()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_spool, "*.json").Order(StringComparer.Ordinal))
            {
                var data = JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Invalid event spool: " + path);
                _events.AppendBatch(data, Path.GetFileNameWithoutExtension(path));
                File.Delete(path);
            }
            HealthChanged?.Invoke(null);
            return null;
        }
        catch (Exception ex)
        {
            Logging.FileLog.Error.Write(ex);
            HealthChanged?.Invoke(ex);
            return ex; // files remain for retry or the next launch
        }
    }

    private async Task RunAsync()
    {
        var batch = new List<ActivityEvent>();
        Request? active = null;
        try
        {
            while (true)
            {
                while (_queue.Reader.TryRead(out active))
                {
                    if (active.Event is { } ev) batch.Add(ev);
                    if (batch.Count >= 256 || active.Barrier is not null) Save(batch);
                    if (active.Barrier is { } barrier)
                    {
                        var error = Replay();
                        if (error is null) barrier.TrySetResult(); else barrier.TrySetException(error);
                    }
                    active = null;
                }
                Save(batch);
                Replay();
                if (_queue.Reader.Completion.IsCompleted) break;
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            FatalError = ex;
            _queue.Writer.TryComplete(ex);
            active?.Barrier?.TrySetException(ex);
            while (_queue.Reader.TryRead(out var request)) request.Barrier?.TrySetException(ex);
            Logging.FileLog.Error.Write(ex);
            HealthChanged?.Invoke(ex);
            throw;
        }
    }

    private sealed class Adapter(BufferedEventWriter owner) : ChannelWriter<ActivityEvent>
    {
        public override bool TryWrite(ActivityEvent item)
        {
            // Apply backpressure instead of silently dropping state boundaries.
            try { owner._queue.Writer.WriteAsync(new(item, null)).AsTask().GetAwaiter().GetResult(); return true; }
            catch (ChannelClosedException) { return false; }
        }
        public override bool TryComplete(Exception? error = null) => owner._queue.Writer.TryComplete(error);
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            owner._queue.Writer.WaitToWriteAsync(cancellationToken);
    }
}
