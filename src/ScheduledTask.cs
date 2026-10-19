using System;
using System.Diagnostics;
using System.Globalization;

namespace MintClicker
{
    // Windows Task Scheduler integration: starts the app at a chosen time. The task is
    // created for the current user and only launches the clicker; it never starts a run.
    internal static class ScheduledTask
    {
        internal const string TaskName = "MintClicker-TimeStart";

        internal static string BuildCreateArguments(string executable, DateTime when, bool daily)
        {
            string time = when.ToString("HH:mm", CultureInfo.InvariantCulture);
            string schedule = daily
                ? "/SC DAILY /ST " + time
                : "/SC ONCE /SD " + when.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + " /ST " + time;
            return "/Create /F /IT /TN \"" + TaskName + "\" /TR " + Quote(executable) + " " + schedule;
        }

        internal static string BuildDeleteArguments()
        {
            return "/Delete /F /TN \"" + TaskName + "\"";
        }

        internal static string BuildQueryArguments()
        {
            return "/Query /TN \"" + TaskName + "\"";
        }

        // schtasks needs escaped quotes around a quoted path so the task target keeps
        // its .exe path intact.
        internal static string Quote(string value)
        {
            return value.IndexOf(' ') >= 0 ? "\"\\\"" + value + "\\\"\"" : "\"" + value + "\"";
        }

        internal static bool TryRun(string arguments, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(info))
                {
                    if (process == null) { output = "无法启动 schtasks.exe"; return false; }
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); }
                        catch (Exception) { }
                        output = "操作超时";
                        return false;
                    }
                    output = (stdout + "\n" + stderr).Trim();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception error)
            {
                output = error.Message;
                return false;
            }
        }

        internal static bool Exists()
        {
            string output;
            return TryRun(BuildQueryArguments(), out output);
        }

        internal static bool Create(string executable, DateTime when, bool daily, out string message)
        {
            string output;
            if (TryRun(BuildCreateArguments(executable, when, daily), out output))
            {
                message = "计划任务已创建";
                return true;
            }
            message = FirstLine(output, "创建计划任务失败");
            return false;
        }

        internal static bool Delete(out string message)
        {
            string output;
            if (TryRun(BuildDeleteArguments(), out output))
            {
                message = "计划任务已删除";
                return true;
            }
            message = FirstLine(output, "删除计划任务失败");
            return false;
        }

        private static string FirstLine(string output, string fallback)
        {
            if (String.IsNullOrEmpty(output)) return fallback;
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed.Length > 220 ? trimmed.Substring(0, 220) : trimmed;
            }
            return fallback;
        }
    }
}
