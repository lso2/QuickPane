using System;
using System.IO;

namespace QuickPane.Services
{
    /// <summary>
    /// Console-style file logger. Writes to %APPDATA%\QuickPane\debug.log, falling back to
    /// %TEMP%\quickpane.log if APPDATA is not writable. It never touches the Windows Event Log.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPane");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "debug.log");
                RollIfLarge();
            }
            catch
            {
                _path = Path.Combine(Path.GetTempPath(), "quickpane.log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        private static void Write(string level, string message)
        {
            if (_path == null) _path = Path.Combine(Path.GetTempPath(), "quickpane.log");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never crash the app. If even the fallback fails, give up silently.
            }
        }

        private static void RollIfLarge()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var bak = _path + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_path, bak);
                }
            }
            catch { }
        }
    }
}
