using System;
using System.IO;

namespace ETab.Helpers;

/// <summary>
/// Minimal, best-effort file logger. Logging must never throw or otherwise
/// affect application behavior.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_logPath == null)
                {
                    _logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "E-Tab",
                        "logs",
                        "E-Tab.log");
                }

                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(
                    _logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort and must never crash the application.
        }
    }
}
