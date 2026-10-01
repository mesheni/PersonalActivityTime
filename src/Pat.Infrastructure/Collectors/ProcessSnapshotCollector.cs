using System.Text.Json;
using System.Threading.Channels;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Win32;

namespace Pat.Infrastructure.Collectors;

/// <summary>
/// Запуск/останов процессов через диф снимков Toolhelp32 (каждые 3 с, без админ-прав).
/// Первый снимок — базлайн без событий, дальше только изменения. Смена exe у живого pid
/// трактуется как stopped+started (PID reuse). Командная строка не собирается:
/// для неё нужен PEB/WMI, что падает на elevated-процессах.
/// Payload: {"pid":..,"ppid":..,"exe":"..","parent":".."} — имя родителя денормализовано
/// из живого снимка, поэтому дерево процессов строится из журнала без пересборки.
/// </summary>
public sealed class ProcessSnapshotCollector : IActivityCollector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;
    private Timer? _timer;

    // pid → (exe, ppid) на момент прошлого снимка
    private Dictionary<uint, (string Exe, uint Ppid)> _previous = [];

    private sealed record ProcessPayload(uint Pid, uint Ppid, string Exe, string? Parent);

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            _writer = writer;
        }
        _timer = new Timer(_ => Snapshot(), null, TimeSpan.Zero, PollInterval);
    }

    void IActivityCollector.Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            _writer = null;
            _previous = [];
        }
    }

    private void Snapshot()
    {
        ChannelWriter<ActivityEvent>? writer;
        lock (_gate)
        {
            writer = _writer;
        }
        if (writer is null)
            return;

        var snapshot = TakeSnapshot();
        if (snapshot is null)
            return;

        lock (_gate)
        {
            var previous = _previous;
            var isFirstSnapshot = previous.Count == 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var current = snapshot.ToDictionary(e => e.Pid, e => (e.Exe, e.Ppid));

            if (!isFirstSnapshot)
            {
                foreach (var (pid, exe, ppid) in snapshot)
                {
                    if (!previous.TryGetValue(pid, out var old))
                    {
                        // новый процесс; имя родителя берём из текущего (живого) снимка
                        var parent = snapshot.FirstOrDefault(e => e.Pid == ppid).Exe;
                        writer.TryWrite(Journal(ActivityEventKind.ProcessStarted, now, pid, ppid, exe, parent));
                    }
                    else if (!string.Equals(old.Exe, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        // pid переехал на другой exe — это другой процесс
                        writer.TryWrite(Journal(ActivityEventKind.ProcessStopped, now, pid, old.Ppid, old.Exe, null));
                        var parent = snapshot.FirstOrDefault(e => e.Pid == ppid).Exe;
                        writer.TryWrite(Journal(ActivityEventKind.ProcessStarted, now, pid, ppid, exe, parent));
                    }
                }

                foreach (var (pid, old) in previous)
                {
                    if (!current.ContainsKey(pid))
                        writer.TryWrite(Journal(ActivityEventKind.ProcessStopped, now, pid, old.Ppid, old.Exe, null));
                }
            }

            _previous = current;
        }
    }

    private static ActivityEvent Journal(
        ActivityEventKind kind, long ts, uint pid, uint ppid, string exe, string? parent) =>
        ActivityEvent.Journal(kind, ts, JsonSerializer.Serialize(new ProcessPayload(pid, ppid, exe, parent), PayloadJson));

    /// <summary>null, если снимок не удался (например, временный отказ API) — просто пропускаем такт.</summary>
    private static List<(uint Pid, string Exe, uint Ppid)>? TakeSnapshot()
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return null;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };
            var result = new List<(uint, string, uint)>(300);
            if (NativeMethods.Process32FirstW(handle, ref entry))
            {
                do
                {
                    result.Add((entry.th32ProcessID, entry.szExeFile, entry.th32ParentProcessID));
                }
                while (NativeMethods.Process32NextW(handle, ref entry));
            }
            return result;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
