using System.Threading.Channels;
using Microsoft.Win32;
using Pat.Core;
using Pat.Core.Events;

namespace Pat.Infrastructure.Collectors;

/// <summary>Блокировка/разблокировка сеанса и сон/пробуждение — границы валидности сессий.</summary>
public sealed class SessionMonitor : IActivityCollector
{
    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            _writer = writer;
        }
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Emit(Win32.NativeMethods.IsWorkstationLocked() ? ActivityEventKind.SessionLocked : ActivityEventKind.SessionUnlocked);
    }

    void IActivityCollector.Stop()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        lock (_gate)
        {
            _writer = null;
        }
    }

    private void Emit(ActivityEventKind kind)
    {
        lock (_gate)
        {
            _writer?.TryWrite(ActivityEvent.System(kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
                Emit(ActivityEventKind.SessionLocked);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
                Emit(ActivityEventKind.SessionUnlocked);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Emit(ActivityEventKind.SystemSuspend);
                break;
            case PowerModes.Resume:
                Emit(ActivityEventKind.SystemResume);
                break;
        }
    }
}
