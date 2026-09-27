using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MintClicker
{
    internal sealed class TestSurface : Panel
    {
        internal int LeftDown, RightDown, MiddleDown, LeftUp, RightUp, MiddleUp;
        internal readonly StringBuilder Sequence = new StringBuilder();
        internal TestSurface() { SetStyle(ControlStyles.StandardDoubleClick, false); BackColor = Color.FromArgb(222, 242, 232); }
        protected override void WndProc(ref Message m)
        {
            // Windows replaces the second DOWN with DBLCLK on a double-click class.
            if (m.Msg == 0x201 || m.Msg == 0x203) LeftDown++;
            if (m.Msg == 0x202) LeftUp++;
            if (m.Msg == 0x204 || m.Msg == 0x206) RightDown++;
            if (m.Msg == 0x205) RightUp++;
            if (m.Msg == 0x207 || m.Msg == 0x209) MiddleDown++;
            if (m.Msg == 0x208) MiddleUp++;
            if (m.Msg == 0x201 || m.Msg == 0x203)
            {
                int x = (short)(m.LParam.ToInt64() & 0xffff);
                Sequence.Append(x < Width / 3 ? 'A' : x < Width * 2 / 3 ? 'B' : 'C');
            }
            base.WndProc(ref m);
        }
        internal void ResetCounts() { LeftDown = RightDown = MiddleDown = LeftUp = RightUp = MiddleUp = 0; Sequence.Clear(); }
    }

    internal sealed class TestPad : Form
    {
        private readonly TestSurface surface = new TestSurface();
        private readonly Label counters = new Label();
        private readonly Label outcome = new Label();
        private readonly Label sequence = new Label();
        private readonly Button run = new Button();
        private readonly Button reset = new Button();
        private readonly ClickEngine engine = new ClickEngine();
        private readonly Timer timer = new Timer();
        private readonly StringBuilder report = new StringBuilder();
        private int test = -1, settle;
        private readonly Stopwatch timeout = new Stopwatch();
        private readonly string[] names = { "left single count", "right single count", "middle single count", "left double count", "minimum interval", "interrupt long wait", "reject invalid interval", "own-window protection", "multi-point order and cycles", "multi-point double clicks", "cancel mid-cycle", "reject empty route", "single-point route", "route snapshot isolation" };

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TestPad());
        }

        internal TestPad()
        {
            Text = "连点器验证窗口";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(540, 485);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(20, 50);
            Font = new Font("Microsoft YaHei UI", 11);
            surface.Bounds = new Rectangle(20, 70, 500, 210);
            Controls.Add(surface);
            Label help = new Label { Text = "绿色区域仅用于接收测试点击，不执行其他操作。", Bounds = new Rectangle(20, 20, 510, 40) };
            Controls.Add(help);
            counters.Bounds = new Rectangle(20, 293, 510, 40);
            Controls.Add(counters);
            sequence.Bounds = new Rectangle(20, 326, 510, 30);
            Controls.Add(sequence);
            run.Text = "运行核心自检";
            run.Bounds = new Rectangle(20, 369, 210, 42);
            run.Click += delegate { report.Clear(); test = 0; run.Enabled = reset.Enabled = false; BeginTest(); };
            Controls.Add(run);
            reset.Text = "清零计数";
            reset.Bounds = new Rectangle(252, 369, 210, 42);
            reset.Click += delegate { surface.ResetCounts(); outcome.Text = "等待测试"; };
            Controls.Add(reset);
            outcome.Bounds = new Rectangle(20, 435, 510, 45);
            outcome.Text = "等待测试";
            Controls.Add(outcome);
            timer.Interval = 50;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        private ClickOptions Options()
        {
            return new ClickOptions { Interval = 40, Button = test == 1 ? 1 : test == 2 ? 2 : 0, DoubleClick = test == 3, Limit = test == 3 ? 4 : 5, FixedPoint = surface.PointToScreen(new Point(250, 100)), Desktop = SystemInformation.VirtualScreen, MainWindow = IntPtr.Zero };
        }

        private void BeginTest()
        {
            surface.ResetCounts();
            settle = 0;
            timeout.Restart();
            ClickOptions options = Options();
            if (test == 4) { options.Interval = 10; options.Limit = 20; }
            if (test == 5) { options.Interval = 30000; options.Limit = 0; }
            if (test == 6 || test == 11)
            {
                if (test == 6) options.Interval = 0;
                else options.Positions = new Point[0];
                try { engine.Start(options); Complete(false, "invalid input accepted"); }
                catch (ArgumentException) { Complete(true, "rejected"); }
                return;
            }
            if (test == 7) options.MainWindow = Handle;
            if (test >= 8)
            {
                Point a = surface.PointToScreen(new Point(surface.Width / 6, surface.Height / 2));
                Point b = surface.PointToScreen(new Point(surface.Width / 2, surface.Height / 2));
                Point c = surface.PointToScreen(new Point(surface.Width * 5 / 6, surface.Height / 2));
                options.Positions = test == 9 ? new[] { a, c } : test == 12 ? new[] { b } : new[] { a, b, c };
                options.Limit = test == 12 ? 3 : 2;
                options.DoubleClick = test == 9;
                if (test == 10) { options.Interval = 30000; options.Limit = 0; }
            }
            engine.Start(options);
            if (test == 13) options.Positions[1] = options.Positions[2];
            outcome.Text = "正在验证：" + names[test];
        }

        private void Tick()
        {
            counters.Text = "按下 / 抬起    左 " + surface.LeftDown + "/" + surface.LeftUp + "    右 " + surface.RightDown + "/" + surface.RightUp + "    中 " + surface.MiddleDown + "/" + surface.MiddleUp;
            sequence.Text = "位置顺序：" + (surface.Sequence.Length > 36 ? surface.Sequence.ToString(0, 36) + "…" : surface.Sequence.ToString());
            if (test < 0) return;
            if (timeout.ElapsedMilliseconds > 6000) { engine.Stop("超时"); Complete(false, "timeout"); return; }
            if ((test == 5 && engine.Rounds >= 1) || (test == 10 && surface.LeftDown >= 1))
            {
                Stopwatch stopTime = Stopwatch.StartNew();
                engine.Stop("cancel-test");
                stopTime.Stop();
                Complete(stopTime.ElapsedMilliseconds < 250 && engine.Rounds == (test == 10 ? 0 : 1) && (test != 10 || surface.Sequence.ToString() == "A"), "stop latency=" + stopTime.ElapsedMilliseconds + "ms; rounds=" + engine.Rounds + "; sequence=" + surface.Sequence);
                return;
            }
            if (engine.Running) return;
            if (++settle < 3) return;
            if (test >= 8)
            {
                string expectedSequence = test == 9 ? "AACCAACC" : test == 12 ? "BBB" : "ABCABC";
                bool routePass = surface.Sequence.ToString() == expectedSequence && engine.Rounds == (test == 12 ? 3 : 2) && surface.LeftUp == expectedSequence.Length;
                Complete(routePass, "sequence=" + surface.Sequence + "; rounds=" + engine.Rounds + "; up=" + surface.LeftUp);
                return;
            }
            int expected = test == 3 ? 8 : test == 4 ? 20 : test == 7 ? 0 : 5;
            int left = test == 1 || test == 2 ? 0 : expected;
            int right = test == 1 ? expected : 0;
            int middle = test == 2 ? expected : 0;
            bool pass = surface.LeftDown == left && surface.LeftUp == left && surface.RightDown == right && surface.RightUp == right && surface.MiddleDown == middle && surface.MiddleUp == middle;
            if (test == 7) pass = pass && engine.Rounds == 0;
            Complete(pass, "left=" + surface.LeftDown + "/" + surface.LeftUp + ", right=" + surface.RightDown + "/" + surface.RightUp + ", middle=" + surface.MiddleDown + "/" + surface.MiddleUp + "; rounds=" + engine.Rounds + "; " + engine.Reason);
        }

        private void Complete(bool pass, string detail)
        {
            report.AppendLine((pass ? "PASS " : "FAIL ") + names[test] + ": " + detail);
            test++;
            if (test >= names.Length)
            {
                report.AppendLine("Native INPUT size=" + Marshal.SizeOf(typeof(Native.Input)) + "; process bits=" + (IntPtr.Size * 8));
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results.txt"), report.ToString(), new UTF8Encoding(true));
                outcome.Text = report.ToString().Contains("FAIL ") ? "存在失败项，请查看 results.txt" : names.Length + " / " + names.Length + " 核心测试通过";
                test = -1;
                run.Enabled = reset.Enabled = true;
            }
            else BeginTest();
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { timer.Dispose(); engine.Dispose(); base.OnFormClosed(e); }
    }
}
