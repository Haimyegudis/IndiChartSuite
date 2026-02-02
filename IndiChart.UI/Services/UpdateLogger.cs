using System;
using System.IO;

namespace IndiChart.UI.Services
{
    public static class UpdateLogger
    {
        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IndiChart",
            "update_debug.log");

        public static void Log(string message)
        {
            try
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, logEntry);
            }
            catch
            {
                // Ignore write errors to avoid crashing the application
            }
        }

        public static void Log(string context, Exception ex)
        {
            Log($"[ERROR] {context}: {ex.Message}\nStack: {ex.StackTrace}");
        }
    }
}
