using System;
using System.IO;

namespace TruckScale.Pos;

/// <summary>
/// Lightweight file logger. Writes to C:\TruckScaleLogs\, one file per day.
/// File name format: log_YYYY-MM-DD.txt  (e.g. log_2026-03-17.txt)
/// Files older than 2 days are deleted automatically on each write.
/// Thread-safe, never throws — logging failures are silently swallowed.
/// </summary>
internal static class AppLogger
{
    private static readonly string _folder = @"C:\TruckScaleLogs";
    private static readonly object _lock = new();

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
                PurgeOldLogs();
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
            var cutoff = DateTime.Now.AddDays(-2);
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
