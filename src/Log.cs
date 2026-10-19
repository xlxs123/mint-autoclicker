using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MintClicker
{
    // Append-only diagnostic log next to the profile library. Every failure here is
    // swallowed: logging must never be the reason the clicker stops working.
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private const int KeepFiles = 3;
        private static readonly object sync = new object();

        internal static string Folder { get { return AppPaths.LogFolder; } }
        internal static string FilePath { get { return Path.Combine(Folder, "app.log"); } }

        internal static void Info(string message) { Write("INFO", message, null); }
        internal static void Error(string message, Exception error) { Write("ERROR", message, error); }

        internal static void Start()
        {
            Info("启动 " + AppInfo.WindowTitle + " · Windows " + Environment.OSVersion.Version + " · CLR " + Environment.Version);
        }

        internal static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Process.Start("explorer.exe", "\"" + Folder + "\"");
            }
            catch (Exception error) { Write("ERROR", "无法打开日志文件夹", error); }
        }

        private static void Write(string level, string message, Exception error)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Folder);
                    Rotate();
                    StringBuilder line = new StringBuilder();
                    line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    line.Append(" [").Append(level).Append("] ").Append(message);
                    if (error != null) line.AppendLine().Append(error.ToString());
                    // UTF-8 with BOM so 记事本 / Windows PowerShell name no encoding
                    // argument and still read the Chinese text correctly.
                    File.AppendAllText(FilePath, line.ToString() + Environment.NewLine, new UTF8Encoding(true));
                }
            }
            catch (Exception) { }
        }

        // app.log -> app.log.1 -> app.log.2 -> app.log.3, oldest dropped.
        private static void Rotate()
        {
            FileInfo file = new FileInfo(FilePath);
            if (!file.Exists || file.Length < MaxBytes) return;
            for (int index = KeepFiles - 1; index >= 1; index--)
            {
                string from = FilePath + "." + index, to = FilePath + "." + (index + 1);
                if (!File.Exists(from)) continue;
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            string first = FilePath + ".1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(FilePath, first);
        }
    }
}
