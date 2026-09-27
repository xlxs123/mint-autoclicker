using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace MintClicker
{
    internal sealed class MarkerReceiver : Panel
    {
        internal readonly StringBuilder Sequence = new StringBuilder();
        internal int LeftClicks, RightClicks, MiddleClicks;
        internal MarkerReceiver() { BackColor = Color.FromArgb(231, 240, 248); }
        protected override void WndProc(ref Message m)
        {
            int mouseButton = m.Msg == 0x201 || m.Msg == 0x203 ? 0 : m.Msg == 0x204 || m.Msg == 0x206 ? 1 : m.Msg == 0x207 || m.Msg == 0x209 ? 2 : -1;
            if (mouseButton >= 0)
            {
                if (mouseButton == 0) LeftClicks++; else if (mouseButton == 1) RightClicks++; else MiddleClicks++;
                int x = (short)(m.LParam.ToInt64() & 0xffff);
                Sequence.Append(x < Width / 3 ? 'A' : x < Width * 2 / 3 ? 'B' : 'C');
            }
            base.WndProc(ref m);
        }
        internal void Reset() { LeftClicks = RightClicks = MiddleClicks = 0; Sequence.Clear(); }
    }

    internal sealed class MarkerTests : Form
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        private readonly MarkerReceiver receiver = new MarkerReceiver();
        private readonly Label result = new Label();
        private readonly Button run = new Button();
        private readonly Timer timer = new Timer();
        private readonly ClickEngine engine = new ClickEngine();
        private readonly Stopwatch timeout = new Stopwatch();
        private readonly StringBuilder report = new StringBuilder();
        private readonly string path = Path.Combine(Path.GetTempPath(), "MintClicker-MarkerTests-" + Guid.NewGuid().ToString("N"), "settings.xml");
        private MainForm preview;
        private Point[] points;
        private bool testing;
        private readonly bool autoRun;
        private int settling;

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MarkerTests(Array.IndexOf(args, "--run") >= 0));
        }
        internal MarkerTests(bool automatic)
        {
            autoRun = automatic;
            Text = "屏幕标记验证";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(440, 390);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(20, 100);
            Controls.Add(new Label { Text = "三枚标记位于下方接收区，测试仅点击本窗口。", Bounds = new Rectangle(15, 20, 410, 40) });
            receiver.Bounds = new Rectangle(15, 70, 410, 190);
            Controls.Add(receiver);
            run.Text = "运行标记验证";
            run.Bounds = new Rectangle(15, 280, 190, 40);
            run.Click += delegate { StartTests(); };
            Controls.Add(run);
            AcceptButton = run;
            result.Text = "等待验证；用户保存的位置不受影响。";
            result.Bounds = new Rectangle(15, 334, 410, 50);
            Controls.Add(result);
            timer.Interval = 50;
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            points = new[] { receiver.PointToScreen(new Point(receiver.Width / 6, receiver.Height / 2)), receiver.PointToScreen(new Point(receiver.Width / 2, receiver.Height / 2)), receiver.PointToScreen(new Point(receiver.Width * 5 / 6, receiver.Height / 2)) };
            ResetPreview();
            if (autoRun) BeginInvoke(new Action(StartTests));
        }
        private void Save(Preferences preferences)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream file = File.Create(path)) new XmlSerializer(typeof(Preferences)).Serialize(file, preferences);
        }
        private void ResetPreview()
        {
            if (preview != null && !preview.IsDisposed) preview.Close();
            Preferences prefs = new Preferences { PositionMode = 2, Limit = 2, ShowMarkers = true };
            for (int i = 0; i < 3; i++) prefs.Positions.Add(new PositionEntry { X = points[i].X, Y = points[i].Y, Button = i });
            Save(prefs);
            OpenPreview();
        }
        private void OpenPreview()
        {
            preview = new MainForm(path, false);
            preview.Text = "轻点 · 标记预览（测试配置）";
            preview.StartPosition = FormStartPosition.Manual;
            preview.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - preview.Width - 15, 35);
            preview.Show();
            // WinForms posts OnShown; allow marker creation before asserting visible UI state.
            Application.DoEvents();
        }
        private static Control Find(Control root, Predicate<Control> predicate)
        {
            foreach (Control control in root.Controls)
            {
                if (predicate(control)) return control;
                Control found = Find(control, predicate);
                if (found != null) return found;
            }
            return null;
        }
        private ListView List { get { return (ListView)Find(preview, delegate(Control c) { return c is ListView; }); } }
        private static List<MarkerWindow> Markers()
        {
            List<MarkerWindow> list = new List<MarkerWindow>();
            foreach (Form form in Application.OpenForms) if (form is MarkerWindow) list.Add((MarkerWindow)form);
            return list;
        }
        private void Select(int index)
        {
            foreach (ListViewItem item in List.Items) item.Selected = false;
            List.Items[index].Selected = true;
        }
        private void ClickButton(string text)
        {
            ((Button)Find(preview, delegate(Control c) { return c is Button && c.Text == text; })).PerformClick();
        }
        private void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            report.AppendLine("PASS " + name);
        }
        private void StartTests()
        {
            run.Enabled = false;
            report.Clear();
            try
            {
                Assert(List.Items.Count == 3 && List.SmallImageList.Images.Count == 3 && Markers().Count == 3, "three list badges and screen markers (items=" + List.Items.Count + ", badges=" + List.SmallImageList.Images.Count + ", markers=" + Markers().Count + ")");
                Assert(Markers()[0].Text == "点击标记 1 · 左键" && Markers()[1].Text == "点击标记 2 · 右键" && Markers()[2].Text == "点击标记 3 · 中键", "numbers and button colors match route");
                receiver.Reset();
                Activate();
                Application.DoEvents();
                if (GetForegroundWindow() != Handle) throw new InvalidOperationException("test receiver must be foreground before sending input");
                engine.Start(new ClickOptions { Interval = 100, Button = 0, Positions = points, PositionButtons = new[] { 0, 1, 2 }, Limit = 2, Desktop = SystemInformation.VirtualScreen, MainWindow = IntPtr.Zero });
                testing = true;
                settling = 0;
                timeout.Restart();
                result.Text = "验证点击穿透与左 / 右 / 中键…";
            }
            catch (Exception ex) { Finish(ex); }
        }
        private void Poll()
        {
            if (!testing) return;
            if (timeout.ElapsedMilliseconds > 5000) { Finish(new Exception("input test timed out")); return; }
            if (engine.Running || ++settling < 3) return;
            testing = false;
            try
            {
                Assert(receiver.LeftClicks == 2 && receiver.RightClicks == 2 && receiver.MiddleClicks == 2 && receiver.Sequence.ToString() == "ABCABC", "real clicks pass through markers in order (2 left, 2 right, 2 middle)");
                Select(1);
                ClickButton("删除选中");
                Assert(List.Items.Count == 2 && List.SmallImageList.Images.Count == 2 && Markers().Count == 2, "deleting point removes corresponding list and screen marker");
                Assert(Markers()[1].Text == "点击标记 2 · 中键" && Markers()[1].Bounds.Contains(points[2]), "old point 3 becomes point 2 with red button unchanged");
                Select(1);
                ClickButton("上移");
                Assert(Markers()[0].Text == "点击标记 1 · 中键" && Markers()[0].Bounds.Contains(points[2]), "reorder updates number at the correct screen coordinate");
                ComboBox edit = (ComboBox)Find(preview, delegate(Control c) { return c.AccessibleName == "选中点击点的鼠标按键"; });
                edit.SelectedIndex = 1;
                Assert(Markers()[0].Text == "点击标记 1 · 右键" && List.Items[0].Text.StartsWith("右键"), "editing a point button updates list and screen marker");
                List.Items[0].Checked = false;
                Assert(Markers().Count == 1 && Markers()[0].Text == "点击标记 2 · 左键", "unchecking hides marker without changing remaining list numbers");
                CheckBox show = (CheckBox)Find(preview, delegate(Control c) { return c.Text == "显示屏幕标记"; });
                show.Checked = false;
                Assert(Markers().Count == 0, "marker visibility switch hides all markers");
                show.Checked = true;
                Assert(Markers().Count == 1, "marker visibility switch restores checked markers");
                preview.Close();
                Assert(Markers().Count == 0, "closing main window removes all overlays");
                Preferences saved;
                using (FileStream file = File.OpenRead(path)) saved = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(file);
                Assert(saved.Positions.Count == 2 && saved.Positions[0].Button == 1 && !saved.Positions[0].Enabled && saved.Positions[0].X == points[2].X, "per-point button order and enabled state persist");
                OpenPreview();
                Assert(List.Items.Count == 2 && List.Items[0].Text.StartsWith("右键") && Markers().Count == 1 && Markers()[0].Text == "点击标记 2 · 左键", "reopening restores saved badges and markers");
                ResetPreview();
                Finish(null);
            }
            catch (Exception ex) { Finish(ex); }
        }
        private void Finish(Exception error)
        {
            string engineResult = "rounds=" + engine.Rounds + "; reason=" + engine.Reason;
            engine.Stop("test finished");
            testing = false;
            if (error != null) report.AppendLine("FAIL " + error.Message + "; received=" + receiver.Sequence + "; counts=" + receiver.LeftClicks + "," + receiver.RightClicks + "," + receiver.MiddleClicks + "; " + engineResult);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "marker-results.txt"), report.ToString(), new UTF8Encoding(true));
            result.Text = error == null ? "13 / 13 标记验证通过" : "验证失败，请查看 marker-results.txt";
            run.Enabled = true;
            if (autoRun) BeginInvoke(new Action(Close));
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose(); engine.Dispose();
            if (preview != null && !preview.IsDisposed) preview.Close();
            base.OnFormClosed(e);
        }
    }
}
