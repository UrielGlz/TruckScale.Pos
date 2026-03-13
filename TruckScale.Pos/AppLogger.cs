using System;
using System.Globalization;
using System.IO;

namespace TruckScale.Pos;

/// <summary>
/// Lightweight file logger. Writes to C:\TruckScaleLogs\, one file per ISO week.
/// File name format: log_YYYY-Www.txt  (e.g. log_2026-W11.txt)
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
            int week = ISOWeek.GetWeekOfYear(now);
            string fileName = $"log_{now.Year}-W{week:D2}.txt";
            string path = Path.Combine(_folder, fileName);
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {message}";

            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }
}
