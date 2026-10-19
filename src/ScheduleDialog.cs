using System;
using System.Drawing;
using System.Windows.Forms;

namespace MintClicker
{
    // Creates or removes the Windows scheduled start registered by ScheduledTask.
    internal sealed class ScheduleDialog : Form
    {
        private readonly ComboBox repeat;
        private readonly DateTimePicker time, date;
        private readonly Label status;

        internal ScheduleDialog()
        {
            Text = "计划任务";
            Font = new Font("Microsoft YaHei UI", 12.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 390);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;

            MainForm.AddLabel(this, "让 Windows 在指定时间启动本程序，到点由系统拉起；不会自动开始点击。", 25, 18, 570, 56, 10, MainForm.Muted, false);
            MainForm.AddLabel(this, "重复方式", 25, 90, 270, 28, 9, MainForm.Muted, false);
            repeat = MainForm.Choice(this, "计划任务重复方式", 25, 118, new[] { "每天", "仅一次" });
            repeat.Width = 270;
            MainForm.AddLabel(this, "启动时间", 320, 90, 270, 28, 9, MainForm.Muted, false);
            time = new DateTimePicker
            {
                Bounds = new Rectangle(320, 118, 270, 38), Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Now.AddMinutes(10)
            };
            Controls.Add(time);
            MainForm.AddLabel(this, "日期（仅一次时使用）", 25, 168, 270, 28, 9, MainForm.Muted, false);
            date = new DateTimePicker { Bounds = new Rectangle(25, 196, 270, 38), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
            Controls.Add(date);
            status = MainForm.AddLabel(this, "当前状态：读取中…", 25, 250, 570, 60, 10, MainForm.Ink, false);

            Button create = MainForm.SmallButton(this, "创建 / 更新", 25, 320, 160, delegate { CreateTask(); });
            Button remove = MainForm.SmallButton(this, "删除任务", 195, 320, 140, delegate { DeleteTask(); });
            Button close = MainForm.SmallButton(this, "关闭", 470, 320, 150, delegate { Close(); });
            create.AccessibleName = "创建计划任务";
            remove.AccessibleName = "删除计划任务";
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;
            repeat.SelectedIndexChanged += delegate { date.Enabled = repeat.SelectedIndex == 1; };
            repeat.SelectedIndex = 0;
            date.Enabled = false;
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            bool exists = ScheduledTask.Exists();
            status.Text = exists
                ? "当前状态：已创建（任务名 " + ScheduledTask.TaskName + "）"
                : "当前状态：还没有计划任务";
        }

        private void CreateTask()
        {
            DateTime when = date.Value.Date + time.Value.TimeOfDay;
            if (repeat.SelectedIndex == 1 && when <= DateTime.Now)
            {
                status.Text = "当前状态：指定的时间已经过去，请选一个未来的时间";
                return;
            }
            string message;
            bool okay = ScheduledTask.Create(Application.ExecutablePath, when, repeat.SelectedIndex == 0, out message);
            Log.Info((okay ? "计划任务已创建：" : "创建计划任务失败：") + message);
            RefreshStatus();
            if (okay)
                MessageBox.Show(this, "已让 Windows 在 " + when.ToString("MM-dd HH:mm") + (repeat.SelectedIndex == 0 ? "（每天）" : "") +
                    " 启动本程序。\n\n到点后程序只会打开窗口，不会自动开始点击。\n如果系统提示权限不足，请先用「以管理员身份重启」再试。",
                    "计划任务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, message, "计划任务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void DeleteTask()
        {
            string message;
            bool okay = ScheduledTask.Delete(out message);
            Log.Info((okay ? "计划任务已删除" : "删除计划任务失败：") + message);
            RefreshStatus();
            MessageBox.Show(this, okay ? "计划任务已删除。" : message, "计划任务", MessageBoxButtons.OK,
                okay ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
