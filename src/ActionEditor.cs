using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MintClicker
{
    internal sealed class KeyCaptureBox : TextBox
    {
        internal int Code;
        internal bool ControlKey, AltKey, ShiftKey;
        internal KeyCaptureBox(MacroEntry entry)
        {
            ReadOnly = true;
            Code = entry.KeyCode; ControlKey = entry.Control; AltKey = entry.Alt; ShiftKey = entry.Shift;
            RefreshText();
        }
        private void RefreshText()
        {
            Text = (ControlKey ? "Ctrl+" : "") + (AltKey ? "Alt+" : "") + (ShiftKey ? "Shift+" : "") + ((Keys)Code).ToString();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            int code = (int)(keyData & Keys.KeyCode);
            if (code != 16 && code != 17 && code != 18)
            {
                Code = code; ControlKey = (keyData & Keys.Control) != 0; AltKey = (keyData & Keys.Alt) != 0; ShiftKey = (keyData & Keys.Shift) != 0;
                RefreshText();
            }
            return true;
        }
    }

    internal sealed class ActionEditor : Form
    {
        private readonly ComboBox kind, button;
        private readonly CheckBox enabled, twice;
        private readonly NumericUpDown x, y, count, interval, duration, wheel, before, after;
        private readonly KeyCaptureBox key;
        internal MacroEntry Result { get; private set; }
        internal ActionEditor(MacroEntry entry)
        {
            Text = "编辑宏步骤";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(492, 548);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            LabelAt("动作类型", 20, 16, 180);
            kind = ChoiceAt(20, 42, 260, new[] { "点击", "等待", "按键", "移动鼠标", "滚轮" }); kind.SelectedIndex = (int)entry.Kind;
            enabled = new CheckBox { Text = "参与执行", Checked = entry.Enabled, Bounds = new Rectangle(300, 43, 168, 28) }; Controls.Add(enabled);
            LabelAt("X 坐标", 20, 82, 216); LabelAt("Y 坐标", 256, 82, 216);
            x = NumberAt(20, 108, -1000000, 1000000, entry.X); y = NumberAt(256, 108, -1000000, 1000000, entry.Y);
            button = ChoiceAt(20, 151, 216, new[] { "左键", "右键", "中键" }); button.SelectedIndex = entry.Button;
            twice = new CheckBox { Text = "每次执行双击", Checked = entry.DoubleClick, Bounds = new Rectangle(256, 151, 216, 28) }; Controls.Add(twice);
            LabelAt("动作次数", 20, 192, 216); LabelAt("动作间隔（毫秒）", 256, 192, 216);
            count = NumberAt(20, 218, 1, 10000, entry.Count); interval = NumberAt(256, 218, 0, 3600000, entry.RepeatInterval);
            LabelAt("等待动作时长（毫秒）", 20, 262, 216); LabelAt("滚轮格数（正上 / 负下）", 256, 262, 216);
            duration = NumberAt(20, 288, 0, 3600000, entry.Duration); wheel = NumberAt(256, 288, -100, 100, entry.Wheel);
            LabelAt("按键：点击输入框后按下所需组合键", 20, 332, 452);
            key = new KeyCaptureBox(entry) { Bounds = new Rectangle(20, 358, 452, 30) }; Controls.Add(key);
            LabelAt("执行前等待（毫秒）", 20, 402, 216); LabelAt("执行后等待（毫秒）", 256, 402, 216);
            before = NumberAt(20, 428, 0, 3600000, entry.DelayBefore); after = NumberAt(256, 428, 0, 3600000, entry.DelayAfter);
            Button okay = new Button { Text = "确定", Bounds = new Rectangle(254, 494, 102, 36) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(368, 494, 104, 36) };
            Controls.Add(okay); Controls.Add(cancel); AcceptButton = okay; CancelButton = cancel;
            okay.Click += delegate
            {
                MacroEntry result = new MacroEntry { Kind = (ActionKind)kind.SelectedIndex, Enabled = enabled.Checked,
                    X = (int)x.Value, Y = (int)y.Value, Button = button.SelectedIndex, DoubleClick = twice.Checked,
                    Count = (int)count.Value, RepeatInterval = (int)interval.Value, Duration = (int)duration.Value, Wheel = (int)wheel.Value,
                    KeyCode = key.Code, Control = key.ControlKey, Alt = key.AltKey, Shift = key.ShiftKey,
                    DelayBefore = (int)before.Value, DelayAfter = (int)after.Value };
                try { ActionCompiler.Validate(result); }
                catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "检查步骤", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Result = result; DialogResult = DialogResult.OK;
            };
            kind.SelectedIndexChanged += delegate { UpdateFields(); };
            UpdateFields();
        }
        private void UpdateFields()
        {
            ActionKind value = (ActionKind)kind.SelectedIndex;
            x.Enabled = y.Enabled = value == ActionKind.Click || value == ActionKind.Move || value == ActionKind.Scroll;
            button.Enabled = twice.Enabled = count.Enabled = interval.Enabled = value == ActionKind.Click;
            duration.Enabled = value == ActionKind.Wait;
            key.Enabled = value == ActionKind.Key;
            wheel.Enabled = value == ActionKind.Scroll;
        }
        private void LabelAt(string text, int x, int y, int width)
        { Controls.Add(new Label { Text = text, Bounds = new Rectangle(x, y, width, 24) }); }
        private NumericUpDown NumberAt(int x, int y, decimal minimum, decimal maximum, decimal value)
        {
            NumericUpDown control = new NumericUpDown { Bounds = new Rectangle(x, y, 216, 30), Minimum = minimum, Maximum = maximum, Value = value, ThousandsSeparator = true };
            Controls.Add(control); return control;
        }
        private ComboBox ChoiceAt(int x, int y, int width, string[] choices)
        {
            ComboBox control = new ComboBox { Bounds = new Rectangle(x, y, width, 30), DropDownStyle = ComboBoxStyle.DropDownList };
            control.Items.AddRange(choices); control.SelectedIndex = 0; Controls.Add(control); return control;
        }
    }
}
