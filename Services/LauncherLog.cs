using System;
using System.IO;
using System.Text;

namespace KVANTLauncher.Services;

/// <summary>
/// Единый логгер ошибок лаунчера. Пишет в %LOCALAPPDATA%\KVANTLauncher\logs\launcher_errors.log.
/// Никогда не бросает исключений (рекурсии нет: catch в самом логгере пустой).
/// </summary>
public static class LauncherLog
{
    private static readonly object _lock = new();

    public static string LogFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KVANTLauncher", "logs", "launcher_errors.log");

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex != null) entry += $" - {ex.GetType().Name}: {ex.Message}";

            lock (_lock)
            {
                string dir = Path.GetDirectoryName(LogFilePath)!;
                Directory.CreateDirectory(dir);
                File.AppendAllText(LogFilePath, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* логгер не должен ронять приложение */ }
    }
}
