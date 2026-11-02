using System.Threading.Channels;
using Pat.Core;
using Pat.Core.Events;
using Pat.Infrastructure.Settings;
using Pat.Infrastructure.Win32;

namespace Pat.Infrastructure.Collectors;

/// <summary>
/// Активное окно. Три источника сигнала, дедуплицируются по (путь exe + нормализованный заголовок):
///  1. WinEventHook EVENT_SYSTEM_FOREGROUND — мгновенно, живёт в собственном потоке с message loop;
///  2. WinEventHook EVENT_OBJECT_NAMECHANGE — смена заголовка при том же окне (переключение файла в IDE);
///  3. страховочный опрос GetForegroundWindow раз в секунду, если хук что-то пропустил.
/// Исключённые процессы дают маркер без имени и заголовка.
/// </summary>
public sealed class ForegroundCollector(SettingsStore settingsStore) : IActivityCollector
{
    private readonly object _gate = new();
    private ChannelWriter<ActivityEvent>? _writer;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Timer? _pollTimer;
    private (string Key, string Title)? _lastEmitted;
    private readonly ManualResetEventSlim _ready = new();

    // Держим ссылки: делегат хука должен пережить поток, иначе GC соберёт и хук умрёт молча.
    private NativeMethods.WinEventDelegate? _hookProc;

    void IActivityCollector.Start(ChannelWriter<ActivityEvent> writer)
    {
        lock (_gate)
        {
            if (_writer is not null) return;
            _writer = writer;
            _lastEmitted = null;
        }

        _ready.Reset();
        _hookThread = new Thread(HookThreadProc) { IsBackground = true, Name = "pat-winevent" };
        _hookThread.Start();
        _ready.Wait();

        _pollTimer = new Timer(_ => Handle(NativeMethods.GetForegroundWindow()), null, 200, 1000);
    }

    void IActivityCollector.Stop()
    {
        if (_pollTimer is { } timer) timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pollTimer = null;
        if (_hookThread is { IsAlive: true } thread)
        {
            if (!NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Unable to stop foreground event thread.");
            thread.Join();
        }
        _hookThread = null;
        lock (_gate)
        {
            _writer = null;
        }
    }

    private void HookThreadProc()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        _hookProc = (hook, eventType, hwnd, idObject, idChild, threadId, timeMs) =>
        {
            // NAMECHANGE летит системно на любой объект; интересен только заголовок текущего окна.
            if (eventType == NativeMethods.EVENT_OBJECT_NAMECHANGE && hwnd != NativeMethods.GetForegroundWindow())
                return;
            if (idObject != 0 /* OBJID_WINDOW */)
                return;
            try { Handle(hwnd); }
            catch (Exception ex) { Logging.FileLog.Error.Write(ex); }
        };

        var foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        var titleHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _hookProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        try
        {
            Handle(NativeMethods.GetForegroundWindow());
            _ready.Set();
            while (NativeMethods.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch (Exception ex) { Logging.FileLog.Error.Write(ex); }
        finally
        {
            if (foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(foregroundHook);
            if (titleHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(titleHook);
            _ready.Set();
        }
    }

    private void Handle(IntPtr hwnd)
    {
        ChannelWriter<ActivityEvent>? writer;
        lock (_gate)
        {
            writer = _writer;
        }
        if (writer is null || hwnd == IntPtr.Zero)
            return;

        // Дедупликация и запись атомарны: хук и опрос могут прийти одновременно
        lock (_gate)
        {
            if (_writer != writer) return;
            var title = NativeMethods.GetWindowTitle(hwnd);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var imagePath = ProcessInfo.GetImagePath(pid);
            var exeName = imagePath is null ? $"pid{pid}" : Path.GetFileNameWithoutExtension(imagePath);

            var excluded = PrivacyPolicy.IsExcluded(settingsStore.Settings, exeName);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (excluded)
            {
                if (_lastEmitted?.Key == "excluded")
                    return;
                _lastEmitted = ("excluded", "");
                writer.TryWrite(ActivityEvent.ExcludedForeground(now));
                return;
            }

            var normTitle = title ?? "";
            var key = imagePath ?? exeName;
            if (_lastEmitted is { } last && last.Key == key && last.Title == normTitle)
                return;
            _lastEmitted = (key, normTitle);

            writer.TryWrite(ActivityEvent.Foreground(now, imagePath ?? exeName, exeName, title ?? ""));
        }
    }
}
