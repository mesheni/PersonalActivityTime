using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Pat.App.Tracking;
using Pat.App.ViewModels;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Logging;
using Pat.Infrastructure.Settings;

namespace Pat.App;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private System.Threading.Timer? _retentionTimer;
    private Mutex? _singleInstance;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "Local\\PersonalActivityTime", out _ownsInstance);
        if (!_ownsInstance) { Shutdown(); return; }

        DispatcherUnhandledException += (_, args) => LogError(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "?"));
        TaskScheduler.UnobservedTaskException += (_, args) => LogError(args.Exception);

        var services = new ServiceCollection();

        var settingsStore = new SettingsStore();
        settingsStore.Load();
        if (settingsStore.LastError is { } settingsError) MessageBox.Show(settingsError +
            (settingsStore.HasUsableSettings ? "" : " Трекинг остановлен до сохранения настроек."), "Восстановление настроек");
        services.AddSingleton(settingsStore);

        // Настройка автозапуска — источник истины: при каждом старте чиним запись
        // в реестре (например, устаревший путь к exe после переезда приложения)
        if (settingsStore.HasUsableSettings) Services.AutoStart.SetEnabled(settingsStore.Settings.StartWithWindows);

        var dbPath = Path.Combine(SettingsStore.AppDataDirectory, "activity.db");
        services.AddSingleton(new SqliteConnectionFactory(dbPath));
        services.AddSingleton<EventRepository>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<SessionMaterializer>();

        services.AddSingleton<ForegroundCollector>();
        services.AddSingleton<IdleMonitor>();
        services.AddSingleton<SessionMonitor>();
        services.AddSingleton<ProcessSnapshotCollector>();
        services.AddSingleton<SystemMetricsCollector>();
        services.AddSingleton<FileActivityCollector>();
        services.AddSingleton<StatsService>();
        services.AddSingleton<TrackingService>();
        services.AddSingleton<Services.BrowserIntegrationService>();
        services.AddSingleton<Services.RetentionService>();
        services.AddSingleton<Services.ExportService>();
        services.AddSingleton<IDayActivityService, DayActivityService>();
        services.AddSingleton<JournalReader>();
        services.AddSingleton<DayContext>();
        services.AddSingleton<JournalViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<TodayViewModel>();
        services.AddSingleton<ReportViewModel>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        new MigrationRunner(_services.GetRequiredService<SqliteConnectionFactory>()).Run();
        if (settingsStore.HasUsableSettings) _services.GetRequiredService<TrackingService>().Start();

        // Очистка по сроку хранения: первый прогон через 2 минуты (не тормозим старт),
        // дальше раз в сутки. RetentionDays = 0 внутри сервиса отключает очистку.
        var retention = _services.GetRequiredService<Services.RetentionService>();
        _retentionTimer = new System.Threading.Timer(
            _ => retention.ApplyRetention(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_retentionTimer is { } timer) timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_services is not null)
        {
            try { _services.GetRequiredService<TrackingService>().StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { LogError(ex); }
            (_services as IDisposable)?.Dispose();
        }
        if (_ownsInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void LogError(Exception? exception) => FileLog.Error.Write(exception?.ToString() ?? "?");

    /// <summary>
    /// Tray-иконка, нарисованная в рантайме: синий квадрат с «часами».
    /// H.NotifyIcon конвертирует ImageSource в .ico только для BitmapImage с UriSource,
    /// поэтому сохраняем PNG в AppData и читаем файлом.
    /// </summary>
    internal static ImageSource CreateAppIcon(int size = 32)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, size, size);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(59, 130, 246)), null,
                rect, size * 0.22, size * 0.22);
            var stroke = new Pen(Brushes.White, Math.Max(2.0, size * 0.08));
            stroke.Freeze();
            dc.DrawEllipse(null, stroke, new Point(size / 2.0, size / 2.0), size * 0.3, size * 0.3);
            dc.DrawLine(stroke, new Point(size / 2.0, size / 2.0), new Point(size / 2.0, size * 0.32));
            dc.DrawLine(stroke, new Point(size / 2.0, size / 2.0), new Point(size * 0.65, size * 0.58));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // H.NotifyIcon читает файл целиком и отдаёт в new Icon(stream) — нужен .ico-контейнер.
        // С Vista иконка может содержать PNG-кадр: 6 байт заголовок + 16 байт директория + PNG.
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(rtb));
        using var pngStream = new MemoryStream();
        pngEncoder.Save(pngStream);
        var png = pngStream.ToArray();

        var ico = new byte[22 + png.Length];
        ico[2] = 1; ico[4] = 1;          // тип: иконка, кадров: 1
        ico[6] = (byte)size;             // ширина
        ico[7] = (byte)size;             // высота
        ico[10] = 1;                     // цветовые плоскости
        ico[12] = 32;                    // бит на пиксель
        BitConverter.TryWriteBytes(ico.AsSpan(14), (uint)png.Length);
        BitConverter.TryWriteBytes(ico.AsSpan(18), 22u); // смещение данных
        png.CopyTo(ico, 22);

        var iconPath = Path.Combine(SettingsStore.AppDataDirectory, "tray.ico");
        Directory.CreateDirectory(SettingsStore.AppDataDirectory);
        File.WriteAllBytes(iconPath, ico);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(iconPath);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
