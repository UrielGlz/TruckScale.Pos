using System;
using System.IO;
using TruckScale.Pos.Config;

namespace TruckScale.Pos;

/// <summary>
/// Lightweight file logger. Writes to C:\TruckScaleLogs\, one file per day.
/// File name format: log_YYYY-MM-DD.txt  (e.g. log_2026-03-17.txt)
/// Files older than N days are deleted (N = config.json "log_retention_days", default 30).
/// The purge runs at most once per day per process, not on every write.
/// Thread-safe, never throws — logging failures are silently swallowed.
/// </summary>
internal static class AppLogger
{
    private static readonly string _folder = @"C:\TruckScaleLogs";
    private static readonly object _lock = new();
    private static DateTime _lastPurgeDate = DateTime.MinValue;

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_folder);

            var now = DateTime.Now;
            string fileName = $"log_{now:yyyy-MM-dd}.txt";
            string path = Path.Combine(_folder, fileName);
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {message}";

            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine);

                if (_lastPurgeDate != now.Date)
                {
                    _lastPurgeDate = now.Date;
                    PurgeOldLogs();
                }
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }

    private static void PurgeOldLogs()
    {
        try
        {
            int days = ConfigManager.Current?.LogRetentionDays ?? AppConfig.DefaultLogRetentionDays;
            if (days < 1) days = AppConfig.DefaultLogRetentionDays;

            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in Directory.GetFiles(_folder, "log_*.txt"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Ignore purge errors.
        }
    }
}
