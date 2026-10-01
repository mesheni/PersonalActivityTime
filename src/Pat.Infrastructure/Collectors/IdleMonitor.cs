using System.Threading.Channels;
using Microsoft.Win32;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Settings;
using Pat.Infrastructure.Win32;

namespace Pat.Infrastructure.Collectors;

/// <summary>
/// Простой: опрос GetLastInputInfo каждые 5 секунд. Содержимое ввода не читается.
///
/// Нюанс secure desktop: ввод пароля на экране блокировки не обновляет last-input интерактивного
/// стола, поэтому после Unlock/Resume монитор сбрасывает своё состояние и emits IdleEnded —
/// иначе флаг «уже idle» залип бы и следующий реальный простой не был бы замечен.
/// Пока сеанс заблокирован/спит — ничего не эмитим (движок и так закрыл сегменты).
/// </summary>
public sealed class IdleMonitor(SettingsStore settingsStore) : IActivityCollector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;
    private Timer? _timer;
    private bool _idleNotified;
    private bool _sessionBlocked;

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            _writer = writer;
            _idleNotified = false;
        }
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, PollInterval);
    }

    void IActivityCollector.Stop()
    {
        _timer?.Dispose();
        _timer = null;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        lock (_gate)
        {
            _writer = null;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                EnterBlocked();
                break;
            case SessionSwitchReason.SessionUnlock:
                ExitBlocked(now);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                EnterBlocked();
                break;
            case PowerModes.Resume:
                ExitBlocked(now);
                break;
        }
    }

    private void EnterBlocked()
    {
        lock (_gate)
        {
            _sessionBlocked = true;
            _idleNotified = false;
        }
    }

    private void ExitBlocked(long nowUtcMs)
    {
        lock (_gate)
        {
            _sessionBlocked = false;
            if (_idleNotified)
            {
                _idleNotified = false;
                _writer?.TryWrite(ActivityEvent.System(ActivityEventKind.IdleEnded, nowUtcMs));
            }
        }
    }

    private void Tick()
    {
        ChannelWriter<ActivityEvent>? writer;
        bool blocked;
        lock (_gate)
        {
            writer = _writer;
            blocked = _sessionBlocked;
        }
        if (writer is null || blocked)
            return;

        var idleMs = GetIdleMilliseconds();
        var thresholdMs = Math.Max(30, settingsStore.Settings.IdleThresholdSeconds) * 1000L;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            if (!_idleNotified && idleMs >= thresholdMs)
            {
                _idleNotified = true;
                // Событие датировано моментом последнего ввода, а не моментом обнаружения
                writer.TryWrite(ActivityEvent.System(ActivityEventKind.IdleBegan, now - idleMs));
            }
            else if (_idleNotified && idleMs < thresholdMs)
            {
                _idleNotified = false;
                // Первый ввод после простоя
                writer.TryWrite(ActivityEvent.System(ActivityEventKind.IdleEnded, now - idleMs));
            }
        }
    }

    private static long GetIdleMilliseconds()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
            return 0;
        // Environment.TickCount (int) и dwTime (uint) мотаются одинаково, поэтому
        // разность int корректна через wrap (аптайм > 24.9 дней не ломает расчёт)
        return Environment.TickCount - unchecked((int)info.dwTime);
    }
}
