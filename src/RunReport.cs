using System;
using System.Text;

namespace MintClicker
{
    // Summary of one finished run, shown by the tray "运行报告" item and written
    // to the log. Kept free of UI code so the formatting can be tested.
    internal sealed class RunReport
    {
        internal DateTime Started;
        internal TimeSpan Duration;
        internal long Rounds, Actions;
        internal string Reason = "";
        internal string Profile = "";
        internal string Mode = "";

        internal string Summary
        {
            get
            {
                return Rounds.ToString("N0") + " 轮 · " + Actions.ToString("N0") + " 次动作 · 用时 " + FormatDuration(Duration) + " · " + Reason;
            }
        }

        internal string Rate
        {
            get
            {
                if (Duration.TotalSeconds < 0.5 || Actions <= 0) return "—";
                return (Actions / Duration.TotalSeconds).ToString("0.0") + " 次/秒";
            }
        }

        internal string Describe()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("方案：" + Profile);
            text.AppendLine("模式：" + Mode);
            text.AppendLine("开始：" + Started.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("用时：" + FormatDuration(Duration));
            text.AppendLine("完成轮数：" + Rounds.ToString("N0"));
            text.AppendLine("执行动作：" + Actions.ToString("N0") + " 次");
            text.AppendLine("平均速度：" + Rate);
            text.Append("结束原因：" + Reason);
            return text.ToString();
        }

        internal static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString("00") + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
            return value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
        }
    }
}
