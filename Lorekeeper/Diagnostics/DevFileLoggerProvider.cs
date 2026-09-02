using System.Text;

namespace Lorekeeper.Diagnostics;

public sealed class DevFileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;
    private const long MaximumTotalBytes = 50L * 1024 * 1024;

    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public DevFileLoggerProvider()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lorekeeper",
            "dev-logs");
        Prune();
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var fileName = $"lorekeeper-dev-{DateTime.Now:yyyyMMdd}.log";
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(categoryName)
                .Append(": ")
                .Append(message)
                .AppendLine();
            if (exception is not null)
                builder.AppendLine(exception.ToString());
            lock (_writeLock)
                File.AppendAllText(Path.Combine(_logDirectory, fileName), builder.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;
            var files = Directory.GetFiles(_logDirectory, "lorekeeper-dev-*.log")
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var remaining = files.Where(file => file.Exists).ToList();
            var totalBytes = remaining.Sum(file => file.Length);
            foreach (var file in remaining)
            {
                if (totalBytes <= MaximumTotalBytes)
                    break;
                try
                {
                    totalBytes -= file.Length;
                    file.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch
        {
        }
    }

    private sealed class Logger(DevFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
