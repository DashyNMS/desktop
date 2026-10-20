using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Appends log lines to %APPDATA%\DashyNMS\logs\dashynms-yyyyMMdd.log.
/// A monitoring tool that sits in the tray for weeks needs somewhere to look
/// when it silently stops fetching, and there is no console to look at.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private readonly LogLevel _minimumLevel;
    private readonly int _retainDays;

    private string? _currentPath;
    private DateOnly _currentDate;

    public FileLoggerProvider(LogLevel minimumLevel = LogLevel.Information, int retainDays = 14)
    {
        _minimumLevel = minimumLevel;
        _retainDays = retainDays;
        PurgeOldLogs();
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder(256);
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(Abbreviate(level));
        builder.Append(' ');
        builder.Append(ShortCategory(category));
        builder.Append(" | ");
        builder.Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        builder.AppendLine();

        lock (_writeLock)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_currentPath is null || today != _currentDate)
                {
                    _currentDate = today;
                    _currentPath = Path.Combine(
                        AppPaths.LogDirectory,
                        "dashynms-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                }

                File.AppendAllText(_currentPath, builder.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never be the reason the app falls over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "dashynms-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "___",
    };

    private static string ShortCategory(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 && index < category.Length - 1 ? category[(index + 1)..] : category;
    }

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
