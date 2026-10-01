using System.Text.Json;
using System.Threading.Channels;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Win32;

namespace Pat.Infrastructure.Collectors;

/// <summary>
/// Системные сэмплы каждые 30 секунд: CPU% через GetSystemTimes (диф между сэмплами —
/// без зависимостей от PerformanceCounter), RAM через GlobalMemoryStatusEx.
/// Payload: {"cpu":%,"ramPct":%,"ramUsedGb":..,"ramTotalGb":..}
/// </summary>
public sealed class SystemMetricsCollector : IActivityCollector
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;
    private Timer? _timer;

    private long _lastIdle;
    private long _lastTotal;

    private sealed record MetricsPayload(double Cpu, double RamPct, double RamUsedGb, double RamTotalGb);

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            _writer = writer;
            // Базовая точка для дифа CPU: первый сэмпл будет иметь корректный знаменатель
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _lastIdle = idle.To64Bit();
                _lastTotal = kernel.To64Bit() + user.To64Bit();
            }
        }
        _timer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
    }

    void IActivityCollector.Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            _writer = null;
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            if (_writer is null)
                return;

            double cpuPercent = 0;
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var idleNow = idle.To64Bit();
                var totalNow = kernel.To64Bit() + user.To64Bit();
                var totalDelta = totalNow - _lastTotal;
                // kernel включает idle: занятость = 1 - Δidle/Δtotal
                if (totalDelta > 0)
                    cpuPercent = Math.Clamp(100.0 * (1 - (idleNow - _lastIdle) / (double)totalDelta), 0, 100);
                _lastIdle = idleNow;
                _lastTotal = totalNow;
            }

            var mem = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };
            double ramPct = 0, ramTotalGb = 0, ramUsedGb = 0;
            if (NativeMethods.GlobalMemoryStatusEx(ref mem))
            {
                ramPct = mem.dwMemoryLoad;
                ramTotalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
                ramUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
            }

            var payload = JsonSerializer.Serialize(
                new MetricsPayload(Math.Round(cpuPercent, 1), Math.Round(ramPct, 1),
                    Math.Round(ramUsedGb, 2), Math.Round(ramTotalGb, 2)),
                PayloadJson);

            _writer.TryWrite(ActivityEvent.Journal(
                ActivityEventKind.SystemSample,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload));
        }
    }
}
