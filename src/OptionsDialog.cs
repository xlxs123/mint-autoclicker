using System;
using System.Drawing;
using System.Windows.Forms;

namespace MintClicker
{
    // The "更多设置" dialog, extracted so the layout test can build it without
    // showing it. Values are read from the controls when the caller accepts.
    internal sealed class OptionsDialog : Form
    {
        private static readonly int[] StartupDelays = { 0, 1, 3, 5, 10 };
        private readonly ComboBox delay;
        private readonly CheckBox protect, precision, minimize, startMin, login, portable, schedule, relative;
        private readonly DateTimePicker scheduleTime;
        private readonly NumericUpDown runMinutes, jitterTime, jitterPoint;
        private int hotStart, hotCapture, hotStop, hotPause, hotCapture2, hotEmergency;

        internal event EventHandler DiagnosticsRequested;

        internal OptionsDialog(Preferences current, bool portableMode, bool loginStartup)
        {
            Text = "更多设置";
            ClientSize = new Size(980, 620);
            Font = MainForm.UiFont(10, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

            // Left column: start, target window, tray, portable.
            MainForm.AddLabel(this, "启动倒计时", 25, 28, 200, 30, 10, MainForm.Muted, false);
            delay = MainForm.Choice(this, "启动倒计时", 25, 58, new[] { "立即开始", "1 秒", "3 秒", "5 秒", "10 秒" });
            delay.Width = 200;
            delay.SelectedIndex = Math.Max(0, Array.IndexOf(StartupDelays, current.StartupDelay));

            protect = MainForm.OptionCheck(this, "仅在绑定的目标窗口处于前台时执行", 25, 112, 440, current.ProtectTarget);
            MainForm.AddLabel(this, current.TargetTitle.Length == 0 ? "尚未绑定目标窗口" : current.TargetTitle, 25, 148, 440, 28, 9, MainForm.Muted, false);
            Button pick = MainForm.SmallButton(this, "从窗口列表选择…", 25, 180, 210, delegate { });
            pick.DialogResult = DialogResult.Yes;
            Button bind = MainForm.SmallButton(this, "3 秒后绑定", 245, 180, 210, delegate { });
            bind.DialogResult = DialogResult.Retry;

            precision = MainForm.OptionCheck(this, "高精度计时（更费电）", 25, 230, 440, current.HighPrecision);
            minimize = MainForm.OptionCheck(this, "关闭或最小化时收进系统托盘", 25, 268, 440, current.TrayOnMinimize);
            startMin = MainForm.OptionCheck(this, "下次启动时最小化（不会自动执行）", 25, 306, 440, current.StartMinimized);
            login = MainForm.OptionCheck(this, "登录 Windows 时启动（当前用户）", 25, 344, 440, loginStartup);
            portable = MainForm.OptionCheck(this, "便携模式：配置放在程序目录（重启生效）", 25, 382, 440, portableMode);
            relative = MainForm.OptionCheck(this, "坐标相对目标窗口（窗口移动后仍可用，需先绑定）", 25, 466, 585, current.RelativeCoordinates);
            MainForm.AddLabel(this, "热键可在右侧「设置热键…」里修改", 25, 508, 585, 28, 9, MainForm.Muted, false);

            // Right column: schedule, run limit, randomization, tools.
            schedule = MainForm.OptionCheck(this, "定时开始（到点自动开始）", 510, 28, 440, current.ScheduleEnabled);
            scheduleTime = new DateTimePicker
            {
                Bounds = new Rectangle(510, 66, 200, 34), Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
                ShowUpDown = true,
                Value = new DateTime(2000, 1, 1, Math.Max(0, Math.Min(23, current.ScheduleHour)), Math.Max(0, Math.Min(59, current.ScheduleMinute)), 0)
            };
            Controls.Add(scheduleTime);
            MainForm.AddLabel(this, "运行时长上限（分钟，0 为不限）", 510, 110, 440, 26, 9, MainForm.Muted, false);
            runMinutes = MainForm.Number(this, "运行时长上限", 510, 138, 200, 0, 10000, current.RunMinutes);
            MainForm.AddLabel(this, "随机化间隔 ± 毫秒（0 为关闭）", 510, 188, 440, 26, 9, MainForm.Muted, false);
            jitterTime = MainForm.Number(this, "随机化间隔", 510, 216, 200, 0, 10000, current.JitterInterval);
            MainForm.AddLabel(this, "随机化坐标 ± 像素（0 为关闭）", 510, 266, 440, 26, 9, MainForm.Muted, false);
            jitterPoint = MainForm.Number(this, "随机化坐标", 510, 294, 200, 0, 500, current.JitterPosition);

            hotStart = current.HotkeyStart; hotCapture = current.HotkeyCapture; hotStop = current.HotkeyStop;
            hotPause = current.HotkeyPause; hotCapture2 = current.HotkeyCapture2; hotEmergency = current.HotkeyEmergency;
            MainForm.SmallButton(this, "设置热键…", 510, 348, 200, delegate { EditHotkeys(); });
            MainForm.SmallButton(this, "打开日志文件夹", 510, 402, 200, delegate { Log.OpenFolder(); });
            MainForm.SmallButton(this, "导出诊断包…", 725, 348, 225, delegate
            {
                EventHandler handler = DiagnosticsRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            });
            Button elevated = MainForm.SmallButton(this, "以管理员身份重启", 725, 402, 225, delegate { });
            elevated.DialogResult = DialogResult.Abort;
            MainForm.SmallButton(this, "计划任务…", 725, 458, 225, delegate
            {
                using (ScheduleDialog dialog = new ScheduleDialog()) dialog.ShowDialog(this);
            });

            Button okay = MainForm.SmallButton(this, "应用", 700, 546, 120, delegate { }); okay.DialogResult = DialogResult.OK;
            Button cancel = MainForm.SmallButton(this, "取消", 830, 546, 120, delegate { }); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = okay; CancelButton = cancel;
        }

        private void EditHotkeys()
        {
            Preferences snapshot = new Preferences
            {
                HotkeyStart = hotStart, HotkeyCapture = hotCapture, HotkeyStop = hotStop,
                HotkeyPause = hotPause, HotkeyCapture2 = hotCapture2, HotkeyEmergency = hotEmergency
            };
            using (HotkeyDialog dialog = new HotkeyDialog(snapshot))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                hotStart = dialog.HotkeyStart; hotCapture = dialog.HotkeyCapture; hotStop = dialog.HotkeyStop;
                hotPause = dialog.HotkeyPause; hotCapture2 = dialog.HotkeyCapture2; hotEmergency = dialog.HotkeyEmergency;
            }
        }

        internal int StartupDelayValue { get { return StartupDelays[delay.SelectedIndex]; } }
        internal bool ProtectTargetValue { get { return protect.Checked; } }
        internal bool HighPrecisionValue { get { return precision.Checked; } }
        internal bool TrayOnMinimizeValue { get { return minimize.Checked; } }
        internal bool StartMinimizedValue { get { return startMin.Checked; } }
        internal bool LoginStartupValue { get { return login.Checked; } }
        internal bool PortableModeValue { get { return portable.Checked; } }
        internal bool RelativeCoordinatesValue { get { return relative.Checked; } }
        internal bool ScheduleEnabledValue { get { return schedule.Checked; } }
        internal int ScheduleHourValue { get { return scheduleTime.Value.Hour; } }
        internal int ScheduleMinuteValue { get { return scheduleTime.Value.Minute; } }
        internal int RunMinutesValue { get { return (int)runMinutes.Value; } }
        internal int JitterIntervalValue { get { return (int)jitterTime.Value; } }
        internal int JitterPositionValue { get { return (int)jitterPoint.Value; } }
        internal int HotkeyStartValue { get { return hotStart; } }
        internal int HotkeyCaptureValue { get { return hotCapture; } }
        internal int HotkeyStopValue { get { return hotStop; } }
        internal int HotkeyPauseValue { get { return hotPause; } }
        internal int HotkeyCapture2Value { get { return hotCapture2; } }
        internal int HotkeyEmergencyValue { get { return hotEmergency; } }
    }
}
