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
        private readonly NumericUpDown x, y, count, interval, duration, wheel, before, after, tolerance, endX, endY;
        private readonly KeyCaptureBox key;
        private readonly Label keyLabel, durationLabel, colorLabel, toleranceLabel, xLabel, yLabel, countLabel, intervalLabel;
        private readonly Panel swatch;
        private readonly TextBox hex;
        private readonly Button pick;
        private readonly Timer pickTimer = new Timer { Interval = 1000 };
        private readonly Func<Point, Point> toStored;
        private int pickTicks;
        private string templateBase64 = "";
        private Bitmap templateBitmap;
        private bool pickTemplate;
        internal MacroEntry Result { get; private set; }

        internal ActionEditor(MacroEntry entry) : this(entry, null) { }

        internal ActionEditor(MacroEntry entry, Func<Point, Point> toStored)
        {
            this.toStored = toStored;
            if (entry.Kind == ActionKind.FindImage && !String.IsNullOrEmpty(entry.Template))
            {
                try { templateBitmap = TemplateImage.Decode(entry.Template); templateBase64 = entry.Template; }
                catch (Exception) { templateBitmap = null; templateBase64 = ""; }
            }
            Text = "编辑宏步骤";
            Font = new Font("Microsoft YaHei UI", 12.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(615, 675);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            LabelAt("动作类型", 25, 18, 225);
            kind = ChoiceAt(25, 48, 325, new[] { "点击", "等待", "按键", "移动鼠标", "滚轮", "等颜色", "拖动", "找图点击" }); kind.SelectedIndex = (int)entry.Kind;
            enabled = new CheckBox { Text = "参与执行", Checked = entry.Enabled, Bounds = new Rectangle(375, 50, 210, 35) }; Controls.Add(enabled);
            xLabel = LabelAt("X 坐标", 25, 98, 270); yLabel = LabelAt("Y 坐标", 320, 98, 270);
            x = NumberAt(25, 128, -1000000, 1000000, entry.X); y = NumberAt(320, 128, -1000000, 1000000, entry.Y);
            button = ChoiceAt(25, 182, 270, new[] { "左键", "右键", "中键" }); button.SelectedIndex = entry.Button;
            twice = new CheckBox { Text = "每次执行双击", Checked = entry.DoubleClick, Bounds = new Rectangle(320, 182, 270, 35) }; Controls.Add(twice);
            countLabel = LabelAt("动作次数", 25, 233, 270); intervalLabel = LabelAt("动作间隔（毫秒）", 320, 233, 270);
            count = NumberAt(25, 263, 1, 10000, entry.Count); interval = NumberAt(320, 263, 0, 3600000, entry.RepeatInterval);
            endX = NumberAt(25, 263, -1000000, 1000000, entry.EndX); endY = NumberAt(320, 263, -1000000, 1000000, entry.EndY);
            endX.Visible = endY.Visible = false;
            durationLabel = LabelAt("等待动作时长（毫秒）", 25, 319, 270);
            LabelAt("滚轮格数（正上 / 负下）", 320, 319, 270);
            duration = NumberAt(25, 349, 0, 3600000, entry.Duration); wheel = NumberAt(320, 349, -100, 100, entry.Wheel);

            keyLabel = LabelAt("按键：点击输入框后按下所需组合键", 25, 404, 565);
            key = new KeyCaptureBox(entry) { Bounds = new Rectangle(25, 434, 565, 38) }; Controls.Add(key);

            colorLabel = LabelAt("颜色：输入 #RRGGBB 或取色", 25, 404, 380);
            swatch = new Panel { Bounds = new Rectangle(25, 434, 70, 38), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(entry.PixelColor) };
            hex = new TextBox { Bounds = new Rectangle(105, 434, 140, 38), MaxLength = 7, Text = ToHex(Color.FromArgb(entry.PixelColor)) };
            pick = new Button { Text = "取色（3 秒）", Bounds = new Rectangle(255, 434, 150, 38) };
            toleranceLabel = LabelAt("容差（0～255）", 415, 404, 175);
            tolerance = NumberAt(415, 434, 0, 255, entry.PixelTolerance);
            tolerance.Width = 175;
            Controls.Add(swatch); Controls.Add(hex); Controls.Add(pick);
            hex.TextChanged += delegate
            {
                try { swatch.BackColor = ColorTranslator.FromHtml(hex.Text.Trim()); }
                catch (Exception) { }
            };
            pick.Click += delegate
            {
                pickTemplate = (ActionKind)kind.SelectedIndex == ActionKind.FindImage;
                pickTicks = 3;
                pick.Enabled = false;
                pick.Text = "3 秒…";
                pickTimer.Start();
            };
            pickTimer.Tick += delegate
            {
                pickTicks--;
                if (pickTicks > 0) { pick.Text = pickTicks + " 秒…"; return; }
                pickTimer.Stop();
                pick.Enabled = true;
                try
                {
                    if (pickTemplate)
                    {
                        Point cursor = Cursor.Position;
                        int side = 80;
                        Rectangle area = new Rectangle(cursor.X - side / 2, cursor.Y - side / 2, side, side);
                        int[] pixels = Native.CaptureScreen(area);
                        if (templateBitmap != null) templateBitmap.Dispose();
                        templateBitmap = TemplateImage.FromPixels(pixels, side, side);
                        templateBase64 = TemplateImage.Encode(templateBitmap);
                        UpdateFields();
                        return;
                    }
                    Point pickPoint = Cursor.Position;
                    SetColor(Native.Pixel(pickPoint));
                    Point stored = toStored == null ? pickPoint : toStored(pickPoint);
                    x.Value = stored.X; y.Value = stored.Y;
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, pickTemplate ? "截取模板" : "取色", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    UpdateFields();
                }
            };

            LabelAt("执行前等待（毫秒）", 25, 489, 270); LabelAt("执行后等待（毫秒）", 320, 489, 270);
            before = NumberAt(25, 519, 0, 3600000, entry.DelayBefore); after = NumberAt(320, 519, 0, 3600000, entry.DelayAfter);
            Button okay = new Button { Text = "确定", Bounds = new Rectangle(318, 598, 128, 45) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(460, 598, 130, 45) };
            Controls.Add(okay); Controls.Add(cancel); AcceptButton = okay; CancelButton = cancel;
            okay.Click += delegate
            {
                Color pixelColor;
                if ((ActionKind)kind.SelectedIndex == ActionKind.Pixel)
                {
                    try { pixelColor = ColorTranslator.FromHtml(hex.Text.Trim()); }
                    catch (Exception)
                    {
                        MessageBox.Show(this, "颜色格式无效，请填写 #RRGGBB。", "检查步骤", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
                    }
                }
                else pixelColor = Color.FromArgb(entry.PixelColor);
                bool findImage = (ActionKind)kind.SelectedIndex == ActionKind.FindImage;
                MacroEntry result = new MacroEntry { Kind = (ActionKind)kind.SelectedIndex, Enabled = enabled.Checked,
                    X = (int)x.Value, Y = (int)y.Value, Button = button.SelectedIndex, DoubleClick = twice.Checked,
                    Count = (int)count.Value, RepeatInterval = (int)interval.Value, Duration = (int)duration.Value, Wheel = (int)wheel.Value,
                    KeyCode = key.Code, Control = key.ControlKey, Alt = key.AltKey, Shift = key.ShiftKey,
                    EndX = (int)endX.Value, EndY = (int)endY.Value,
                    Template = findImage ? templateBase64 : "",
                    TemplateWidth = findImage && templateBitmap != null ? templateBitmap.Width : 0,
                    TemplateHeight = findImage && templateBitmap != null ? templateBitmap.Height : 0,
                    PixelColor = pixelColor.ToArgb(), PixelTolerance = (int)tolerance.Value,
                    DelayBefore = (int)before.Value, DelayAfter = (int)after.Value };
                try { ActionCompiler.Validate(result); }
                catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "检查步骤", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Result = result; DialogResult = DialogResult.OK;
            };
            kind.SelectedIndexChanged += delegate { UpdateFields(); };
            FormClosed += delegate { pickTimer.Stop(); pickTimer.Dispose(); if (templateBitmap != null) templateBitmap.Dispose(); };
            UpdateFields();
        }

        private void SetColor(Color color)
        {
            swatch.BackColor = color;
            hex.Text = ToHex(color);
        }

        private static string ToHex(Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private void UpdateFields()
        {
            ActionKind value = (ActionKind)kind.SelectedIndex;
            bool pixel = value == ActionKind.Pixel, isKey = value == ActionKind.Key, drag = value == ActionKind.Drag, find = value == ActionKind.FindImage;
            x.Enabled = y.Enabled = value == ActionKind.Click || value == ActionKind.Move || value == ActionKind.Scroll || pixel || drag;
            button.Enabled = value == ActionKind.Click || drag || find;
            twice.Visible = value == ActionKind.Click || find;
            twice.Enabled = twice.Visible;
            count.Visible = interval.Visible = value == ActionKind.Click;
            endX.Visible = endY.Visible = drag;
            countLabel.Text = drag ? "终点 X 坐标" : "动作次数";
            intervalLabel.Text = drag ? "终点 Y 坐标" : "动作间隔（毫秒）";
            xLabel.Text = drag ? "起点 X 坐标" : "X 坐标";
            yLabel.Text = drag ? "起点 Y 坐标" : "Y 坐标";
            duration.Enabled = value == ActionKind.Wait || pixel || drag || find;
            durationLabel.Text = pixel ? "等颜色超时（0 不限）" : drag ? "按住时长（毫秒）" : find ? "等待图片超时（0 不限）" : "等待动作时长（毫秒）";
            keyLabel.Visible = key.Visible = isKey;
            colorLabel.Visible = swatch.Visible = pick.Visible = tolerance.Visible = toleranceLabel.Visible = pixel || find;
            hex.Visible = pixel;
            toleranceLabel.Text = "容差（0～255）";
            if (pixel)
            {
                colorLabel.Text = "颜色：输入 #RRGGBB 或取色";
                pick.Text = "取色（3 秒）";
                swatch.BackgroundImage = null;
            }
            else if (find)
            {
                colorLabel.Text = templateBitmap == null ? "模板：未截取（鼠标中心 80×80）" : "模板：" + templateBitmap.Width + "×" + templateBitmap.Height + "，可重截";
                pick.Text = "截模板 3 秒";
                swatch.BackColor = Color.White;
                swatch.BackgroundImage = templateBitmap;
                swatch.BackgroundImageLayout = ImageLayout.Zoom;
            }
            wheel.Enabled = value == ActionKind.Scroll;
        }

        private Label LabelAt(string text, int x, int y, int width)
        {
            Label label = new Label { Text = text, Bounds = new Rectangle(x, y, width, 30) };
            Controls.Add(label);
            return label;
        }
        private NumericUpDown NumberAt(int x, int y, decimal minimum, decimal maximum, decimal value)
        {
            NumericUpDown control = new NumericUpDown { Bounds = new Rectangle(x, y, 270, 38), Minimum = minimum, Maximum = maximum, Value = value, ThousandsSeparator = true };
            Controls.Add(control); return control;
        }
        private ComboBox ChoiceAt(int x, int y, int width, string[] choices)
        {
            ComboBox control = new ComboBox { Bounds = new Rectangle(x, y, width, 38), DropDownStyle = ComboBoxStyle.DropDownList };
            control.Items.AddRange(choices); control.SelectedIndex = 0; Controls.Add(control); return control;
        }
    }
}
