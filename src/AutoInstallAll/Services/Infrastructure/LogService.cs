using System.Text;

namespace AutoInstallAll.Services.Infrastructure;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>
/// Ghi nhật ký ra .\Logs\{category}_{yyyy-MM-dd}.log trên USB và phát sự kiện cho màn hình Activity Log.
/// </summary>
public sealed class LogService
{
    private readonly StoragePathManager _paths;
    private readonly object _sync = new();

    public event EventHandler<LogEntry>? EntryWritten;

    public LogService(StoragePathManager paths) => _paths = paths;

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public void Warn(string category, string message, Exception? ex = null) => Write(LogLevel.Warning, category, message, ex);

    public void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    public void Write(LogLevel level, string category, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");
        var line = new StringBuilder()
            .Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(entry.Message);
        if (ex is not null && level == LogLevel.Error)
            line.AppendLine().Append(ex);

        try
        {
            lock (_sync)
                File.AppendAllText(_paths.GetLogPath(category), line.AppendLine().ToString(), Encoding.UTF8);
        }
        catch (Exception ioEx) when (ioEx is IOException or UnauthorizedAccessException)
        {
            // Không để lỗi ghi log (USB bị rút / khoá ghi) làm hỏng luồng cài đặt.
            System.Diagnostics.Debug.WriteLine($"Log write failed: {ioEx.Message}");
        }

        EntryWritten?.Invoke(this, entry);
    }
}
