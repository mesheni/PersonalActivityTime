using System.IO;
using Pat.Infrastructure.Settings;

namespace Pat.Infrastructure.Logging;

/// <summary>
/// Простой файловый лог с ротацией: пишем в %LOCALAPPDATA%\PersonalActivityTime\&lt;fileName&gt;,
/// при превышении 1 МБ текущий файл переезжает в &lt;fileName&gt;.1 (предыдущий .1 затирается).
/// Потокобезопасен внутри процесса; файл открывается с FileShare.ReadWrite, поэтому
/// приложение и NativeHost не мешают друг другу даже при записи в один файл.
/// Ошибка логирования никогда не бросается наружу.
/// </summary>
public sealed class FileLog
{
    private const long MaxBytes = 1_000_000;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _rotatedPath;

    public FileLog(string fileName)
    {
        var directory = SettingsStore.AppDataDirectory;
        _path = Path.Combine(directory, fileName);
        _rotatedPath = _path + ".1";
    }

    /// <summary>Общий журнал ошибок приложения.</summary>
    public static FileLog Error { get; } = new("error.log");

    public void Write(Exception exception) => Write(exception.ToString());

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(line);
            }
            catch
            {
                // лог не должен ронять приложение
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length <= MaxBytes)
                return;
            if (File.Exists(_rotatedPath))
                File.Delete(_rotatedPath);
            File.Move(_path, _rotatedPath);
        }
        catch
        {
            // не смогли отротировать (например, файл занят вторым процессом) —
            // допишем в текущий и попробуем при следующей записи
        }
    }
}
