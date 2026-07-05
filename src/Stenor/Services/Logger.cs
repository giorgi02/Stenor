using System.IO;

namespace Stenor.Services;

/// <summary>
/// Minimal rotating file logger. Writes to %APPDATA%\Stenor\logs\stenor.log, rotating at
/// 512 KB and keeping 3 old files. Must never receive audio content, transcripts, or the
/// API key - callers log event names and error types only.
/// </summary>
public sealed class Logger
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxArchivedFiles = 3;

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _file;

    public Logger()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stenor", "logs");
        _file = Path.Combine(_directory, "stenor.log");
    }

    public void Info(string message) => Write("INF", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + "    " + ex.GetType().Name + ": " + ex.Message;
                    if (ex.StackTrace is { } st)
                    {
                        line += Environment.NewLine + st;
                    }
                }
                File.AppendAllText(_file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_file);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        for (var i = MaxArchivedFiles; i >= 1; i--)
        {
            var src = i == 1 ? _file : Path.Combine(_directory, $"stenor.{i - 1}.log");
            var dst = Path.Combine(_directory, $"stenor.{i}.log");
            if (File.Exists(dst))
            {
                File.Delete(dst);
            }
            if (File.Exists(src))
            {
                File.Move(src, dst);
            }
        }
    }
}
