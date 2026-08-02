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

    private readonly object _syncRoot = new();
    private readonly string _logDirectoryPath;
    private readonly string _logFilePath;

    public Logger()
    {
        _logDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stenor", "logs");
        _logFilePath = Path.Combine(_logDirectoryPath, "stenor.log");
    }

    public void Info(string message) => Write("INF", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_syncRoot)
            {
                Directory.CreateDirectory(_logDirectoryPath);
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
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void RotateIfNeeded()
    {
        var logFileInfo = new FileInfo(_logFilePath);
        if (!logFileInfo.Exists || logFileInfo.Length < MaxFileBytes)
        {
            return;
        }

        for (var i = MaxArchivedFiles; i >= 1; i--)
        {
            var sourcePath = i == 1
                ? _logFilePath
                : Path.Combine(_logDirectoryPath, $"stenor.{i - 1}.log");
            var destinationPath = Path.Combine(_logDirectoryPath, $"stenor.{i}.log");
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destinationPath);
            }
        }
    }
}
