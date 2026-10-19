using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MintClicker
{
    // Read-only box that captures one combination as (modifiers << 16) | virtual key.
    internal sealed class HotkeyBox : TextBox
    {
        internal int Value;
        internal HotkeyBox(int value)
        {
            ReadOnly = true;
            Value = value;
            RefreshText();
        }
        internal static string Describe(int value)
        {
            int key = value & 0xFFFF;
            if (key == 0) return "未设置";
            int mods = (value >> 16) & 0xF;
            return ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 1) != 0 ? "Alt+" : "") + ((mods & 4) != 0 ? "Shift+" : "") + ((Keys)key).ToString();
        }
        private void RefreshText() { Text = Describe(Value); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            int key = (int)(keyData & Keys.KeyCode);
            if (key == 16 || key == 17 || key == 18) return true;
            int mods = ((keyData & Keys.Control) != 0 ? 2 : 0) | ((keyData & Keys.Alt) != 0 ? 1 : 0) | ((keyData & Keys.Shift) != 0 ? 4 : 0);
            Value = (mods << 16) | key;
            RefreshText();
            return true;
        }
    }

    internal sealed class HotkeyDialog : Form
    {
        private readonly HotkeyBox start, capture, stop, pause, capture2, emergency;
        internal int HotkeyStart { get { return start.Value; } }
        internal int HotkeyCapture { get { return capture.Value; } }
        internal int HotkeyStop { get { return stop.Value; } }
        internal int HotkeyPause { get { return pause.Value; } }
        internal int HotkeyCapture2 { get { return capture2.Value; } }
        internal int HotkeyEmergency { get { return emergency.Value; } }

        internal HotkeyDialog(Preferences current)
        {
            Text = "全局热键";
            Font = new Font("Microsoft YaHei UI", 12.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            AddLabel("开始 / 停止", 25, 14, 320);
            AddLabel("取点（主）", 25, 82, 320);
            AddLabel("停止", 25, 150, 320);
            AddLabel("暂停 / 恢复", 25, 218, 320);
            AddLabel("取点（备用）", 25, 286, 320);
            AddLabel("紧急停止", 25, 354, 320);
            start = Box(current.HotkeyStart, 25, 42);
            capture = Box(current.HotkeyCapture, 25, 110);
            stop = Box(current.HotkeyStop, 25, 178);
            pause = Box(current.HotkeyPause, 25, 246);
            capture2 = Box(current.HotkeyCapture2, 25, 314);
            emergency = Box(current.HotkeyEmergency, 25, 382);
            Label hint = new Label
            {
                Text = "点输入框后按下组合键；留空或重复会被拒绝，改动在「应用」后生效。",
                Bounds = new Rectangle(25, 432, 570, 56), ForeColor = Color.FromArgb(101, 117, 110)
            };
            Controls.Add(hint);
            Button reset = new Button { Text = "恢复默认", Bounds = new Rectangle(25, 496, 140, 44) };
            Button okay = new Button { Text = "确定", Bounds = new Rectangle(330, 496, 120, 44) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(460, 496, 130, 44) };
            Controls.Add(reset); Controls.Add(okay); Controls.Add(cancel);
            AcceptButton = okay; CancelButton = cancel;
            reset.Click += delegate
            {
                Preferences defaults = new Preferences();
                start.Value = defaults.HotkeyStart; capture.Value = defaults.HotkeyCapture; stop.Value = defaults.HotkeyStop;
                pause.Value = defaults.HotkeyPause; capture2.Value = defaults.HotkeyCapture2; emergency.Value = defaults.HotkeyEmergency;
                start.Text = HotkeyBox.Describe(start.Value); capture.Text = HotkeyBox.Describe(capture.Value);
                stop.Text = HotkeyBox.Describe(stop.Value); pause.Text = HotkeyBox.Describe(pause.Value);
                capture2.Text = HotkeyBox.Describe(capture2.Value); emergency.Text = HotkeyBox.Describe(emergency.Value);
            };
            okay.Click += delegate
            {
                int[] values = { start.Value, capture.Value, stop.Value, pause.Value, capture2.Value, emergency.Value };
                HashSet<int> seen = new HashSet<int>();
                foreach (int value in values)
                {
                    if ((value & 0xFFFF) == 0)
                    {
                        MessageBox.Show(this, "每个热键都需要一个按键。", "全局热键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!seen.Add(value))
                    {
                        MessageBox.Show(this, "两个热键用了同一个组合，请改掉其中一个。", "全局热键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                DialogResult = DialogResult.OK;
            };
        }

        private Label AddLabel(string text, int x, int y, int width)
        {
            Label label = new Label { Text = text, Bounds = new Rectangle(x, y, width, 26) };
            Controls.Add(label);
            return label;
        }

        private HotkeyBox Box(int value, int x, int y)
        {
            HotkeyBox box = new HotkeyBox(value) { Bounds = new Rectangle(x, y, 570, 38) };
            Controls.Add(box);
            return box;
        }
    }
}
