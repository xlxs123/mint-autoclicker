using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

// Assembly metadata and the version string live in Version.cs (AppInfo).

namespace MintClicker
{
    public sealed class Preferences
    {
        public int Interval = 100, Button, Mode;
        public long Limit;
        public bool Fixed, Pinned;
        public int X, Y;
        public bool? HasFixedPoint;
        public int PositionMode = -1;
        public bool ShowMarkers = true;
        public bool DragMarkers = true;
        public List<PositionEntry> Positions = new List<PositionEntry>();
        public List<MacroEntry> Actions = new List<MacroEntry>();
        public int StartupDelay = 3;
        public bool TrayOnMinimize = true, StartMinimized, ProtectTarget;
        public bool HighPrecision = true;
        // When true, all stored points are client-area offsets of the bound target window.
        public bool RelativeCoordinates;
        public string TargetTitle = "", TargetProcess = "";
        public int LoopStart, LoopEnd, LoopCount = 1;
        // Randomization: 0 disables. Interval is a ± millisecond window, position a ± pixel box.
        public int JitterInterval, JitterPosition;
        // 0 = run until stopped; otherwise the run stops itself after this many minutes.
        public int RunMinutes;
        // Optional wall-clock start. -1 hour means "not scheduled".
        public bool ScheduleEnabled;
        public int ScheduleHour = 9, ScheduleMinute;
        // Global hotkeys, packed as (modifiers << 16) | virtual key. 0 disables one.
        public int HotkeyStart = 0x0075;            // F6
        public int HotkeyCapture = 0x0076;          // F7
        public int HotkeyStop = 0x0077;             // F8
        public int HotkeyPause = 0x0078;            // F9
        public int HotkeyCapture2 = 0x00030041;     // Ctrl+Alt+A
        public int HotkeyEmergency = 0x007B;        // F12
    }

    public sealed class PositionEntry
    {
        public int X, Y;
        public bool Enabled = true;
        public int Button = -1;
        public int DelayBefore;
        public int DelayAfter = -1;
        public int Count = 1, RepeatInterval = 100;
        // -1 keeps the global single/double click choice, 0 forces a single click, 1 a double.
        public int DoubleClick = -1;
        public string Label = "";
    }

    internal static class Program
    {
        private const string MutexName = "Local\\MintClicker_1";

        [STAThread]
        private static void Main(string[] args)
        {
            // "--relaunch" is passed by the administrator restart: the old instance is
            // still closing, so wait for the single-instance mutex instead of bouncing.
            bool relaunch = args != null && Array.IndexOf(args, "--relaunch") >= 0;
            bool first;
            Mutex mutex = new Mutex(true, MutexName, out first);
            if (!first && relaunch)
            {
                for (int attempt = 0; attempt < 40 && !first; attempt++)
                {
                    Thread.Sleep(200);
                    mutex.Dispose();
                    mutex = new Mutex(true, MutexName, out first);
                }
                if (!first) Log.Info("等待上一个实例退出的时间已过，将直接启动。");
            }
            using (mutex)
            {
                if (!first && !relaunch)
                {
                    // No dialog: wake the instance that is already running instead.
                    Log.Info("已有实例在运行，已请求它显示窗口。");
                    Native.RequestActivate();
                    return;
                }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args2) { ReportUnexpected(args2.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args2) { Log.Error("后台线程的未处理异常", args2.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Start();
                try
                {
                    Application.Run(new MainForm());
                    Log.Info("已退出");
                }
                catch (Exception error)
                {
                    // A failure while the window is still being built never reaches the
                    // message loop, so the thread-exception handler cannot help.
                    Log.Error("启动失败", error);
                    MessageBox.Show("程序启动失败：\n" + error.Message + "\n\n详细信息已记录到日志：\n" + Log.FilePath,
                        AppInfo.ShortName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // A failed UI action must not take the whole clicker down: log it, tell the
        // user in plain words, and let the window keep running.
        private static void ReportUnexpected(Exception error)
        {
            Log.Error("界面线程异常", error);
            try
            {
                MessageBox.Show("刚才的操作没有完成，程序仍在运行。\n\n" + error.Message + "\n\n详细信息已记录到：\n" + Log.FilePath,
                    AppInfo.ShortName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception) { }
        }
    }

    internal sealed partial class MainForm : Form
    {
        internal static readonly Color Ink = Color.FromArgb(28, 42, 39);
        internal static readonly Color Muted = Color.FromArgb(101, 117, 110);
        private static readonly Color Green = Color.FromArgb(28, 112, 83);

        // Windows renders these 9-12 pt fonts at 120 DPI (125% display scale), 25% larger
        // than the original 96 DPI design assumed. The window is therefore laid out at
        // 1.25x: every Bounds value below is the 96 DPI design value times Scale, and
        // every font size goes through UiFont so text and its box stay in proportion.
        private const float UiScale = 1.25F;
        private const string UiFontName = "Microsoft YaHei UI";

        internal static Font UiFont(float designPoints, FontStyle style)
        {
            return new Font(UiFontName, designPoints * UiScale, style);
        }
        private readonly ClickEngine engine = new ClickEngine();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NumericUpDown interval, limit, pointX, pointY, delayBefore, delayAfter;
        private readonly ComboBox button, mode, locationMode, pointButton, pointMode, profileChoice;
        private readonly TextBox pointLabel;
        private readonly CheckBox pin, showMarkers, inheritInterval, dragMarkers;
        private readonly Label status, count, elapsed, position, hint, rate;
        private readonly Button start, stopButton, editPoints, applyCoordinates;
        private readonly Panel settings, positionsPanel, profilePanel;
        private Panel dashboardPanel;
        private bool layoutReady, fullScreen, applyingScale;
        private float currentScale = 1f;
        // Scale the layout is heading for. A user resize recomputes it from the window; a
        // mode change (full screen / back to design) sets it explicitly.
        private float requestedScale = 1f;
        private bool resizing;
        private const int DesignWidth = 1325, DesignHeight = 873;
        // Frame thickness of a Sizable window at this DPI (18 px per side, 39 px tall).
        private const int WindowChromeWidth = 36, WindowChromeHeight = 39;
        // Beyond this the text stops being readable; the window simply keeps its default
        // size instead of zooming further.
        private const float MaximumUiScale = 1.8f;
        private Button fullScreenButton;
        private Rectangle restoreBounds;
        private bool restoreTopMost;
        private string hintDefaultText = "";
        private Color hintDefaultColor = Muted;
        private readonly ListView positionsList;
        private ImageList badgeImages;
        private readonly BadgeCache badgeCache = new BadgeCache();
        private readonly BadgeCache macroBadgeCache = new BadgeCache();
        private readonly MarkerLayer markerLayer = new MarkerLayer();
        private bool markersReady, updatingPointButton, markersDirty;
        private Point relativeOrigin;
        private bool relativeOriginKnown;
        private int reorderFrom = -1;
        private Point reorderStart;
        private bool reorderArmed;
        private readonly List<PositionEntry> positions = new List<PositionEntry>();
        private bool updatingPositions, capturing;
        private readonly string configPath;
        private readonly bool useHotkeys;
        private Point? selectedPoint;
        private bool pending, wasRunning, hotkeysReady;
        private readonly System.Diagnostics.Stopwatch countdown = new System.Diagnostics.Stopwatch();
        private ClickOptions pendingOptions;
        private readonly string profilesPath;
        private ProfileLibrary library;
        private bool changingProfile, profileStoreBlocked, modalOpen, uiBusy;
        // Auto-save state: edits set dirty, the UI timer writes them back after a pause.
        private bool dirty, loadingPreferences;
        private DateTime lastAutoSaveAttempt = DateTime.MinValue;
        internal int AutoSaveSeconds = 15;
        private RunReport lastReport;
        private DateTime runStarted;
        private bool editingPoints = true;
        private DateTime scheduledStart = DateTime.MinValue;
        private readonly Recorder recorder;

        internal MainForm() : this(AppPaths.SettingsFile, true) { }

        // An isolated config lets integration tests exercise the real UI without touching saved user points.
        internal MainForm(string settingsPath, bool registerHotkeys)
        {
            configPath = settingsPath;
            profilesPath = Path.ChangeExtension(settingsPath, ".profiles.v2.json");
            useHotkeys = registerHotkeys;
            recorder = new Recorder(delegate { return Handle; }, IsReservedRecordingKey);
            Text = AppInfo.WindowTitle;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            // 860 content + ~29 px of window chrome = 889, which fits the 912 px work area.
            // Never raise this without re-checking: exceeding the work area makes WinForms
            // clamp the window and add a scrollbar, which hides the bottom of the form.
            ClientSize = new Size(1325, 873);
            // The design canvas. The window can be stretched or put in full screen; the
            // layout stretches the two main columns and moves the bottom rows accordingly.
            BackColor = Color.FromArgb(244, 247, 244);
            ForeColor = Ink;
            Font = UiFont(10F, FontStyle.Regular);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            KeyPreview = true;
            DoubleBuffered = true;
            using (Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) Icon = (Icon)icon.Clone();

            AddLabel(this, "轻点", 35, 15, 300, 72, 24, Ink, true);
            AddLabel(this, "让重复点击，简单一点。", 38, 83, 450, 29, 10, Muted, false);
            pin = new CheckBox { Text = "窗口置顶", Bounds = new Rectangle(580, 46, 138, 38), AutoSize = false };
            pin.CheckedChanged += delegate { TopMost = pin.Checked; };
            Controls.Add(pin);
            showMarkers = new CheckBox { Text = "显示屏幕标记", Checked = true, Bounds = new Rectangle(975, 46, 165, 38) };
            showMarkers.CheckedChanged += delegate { SyncMarkers(); };
            Controls.Add(showMarkers);
            dragMarkers = new CheckBox { Text = "停止时允许拖动", Checked = true, Bounds = new Rectangle(1022, 85, 190, 30), Font = UiFont(9, FontStyle.Regular) };
            dragMarkers.CheckedChanged += delegate { SetPointEditing(dragMarkers.Checked); };
            Controls.Add(dragMarkers);

            profilePanel = Card(30, 118, 1240, 48, Color.White);
            AddLabel(profilePanel, "方案", 14, 8, 66, 32, 10, Muted, false);
            profileChoice = Choice(profilePanel, "当前方案", 84, 6, new[] { "默认方案" });
            profileChoice.Width = 297;
            profileChoice.SelectedIndexChanged += delegate { SwitchProfile(); };
            SmallButton(profilePanel, "保存", 390, 6, 98, delegate { if (SaveCurrentProfile()) status.Text = "●  方案已保存"; });
            SmallButton(profilePanel, "新建", 495, 6, 98, delegate { CreateProfile(false); });
            SmallButton(profilePanel, "复制", 600, 6, 98, delegate { CreateProfile(true); });
            SmallButton(profilePanel, "重命名", 705, 6, 110, delegate { RenameProfile(); });
            SmallButton(profilePanel, "删除", 822, 6, 98, delegate { DeleteProfile(); });
            SmallButton(profilePanel, "导入", 927, 6, 98, delegate { ImportProfile(); });
            SmallButton(profilePanel, "导出", 1032, 6, 98, delegate { ExportProfile(); });

            Panel dashboard = Card(30, 180, 690, 145, Color.FromArgb(227, 239, 231));
            dashboardPanel = dashboard;
            status = AddLabel(dashboard, "●  已就绪", 23, 16, 638, 38, 13, Green, true);
            AddLabel(dashboard, "已执行轮数", 25, 60, 188, 25, 9, Muted, false);
            count = AddLabel(dashboard, "0", 25, 86, 294, 54, 20, Ink, true);
            AddLabel(dashboard, "运行时间", 379, 60, 225, 25, 9, Muted, false);
            elapsed = AddLabel(dashboard, "00:00:00", 379, 86, 283, 52, 17, Ink, false);

            settings = Card(30, 345, 690, 305, Color.White);
            AddLabel(settings, "点击设置", 23, 16, 250, 34, 12, Ink, true);
            AddLabel(settings, "全局间隔（毫秒）", 25, 64, 275, 28, 10, Muted, false);
            interval = Number(settings, "点击间隔（毫秒）", 25, 98, 298, 10, 3600000, 100);
            rate = AddLabel(settings, "设定约 10.0 次/秒", 368, 104, 294, 30, 10, Muted, false);
            interval.ValueChanged += delegate { UpdateRateText(); };
            AddLabel(settings, "鼠标按键", 25, 156, 298, 29, 10, Muted, false);
            AddLabel(settings, "点击方式", 368, 156, 298, 29, 10, Muted, false);
            button = Choice(settings, "鼠标按键", 25, 191, new[] { "左键", "右键", "中键" });
            button.SelectedIndexChanged += delegate { SyncMarkers(); };
            mode = Choice(settings, "点击方式", 368, 191, new[] { "单击", "双击（每点 2 次）" });
            AddLabel(settings, "重复次数", 25, 256, 138, 33, 10, Muted, false);
            limit = Number(settings, "重复次数，0 为不限", 163, 253, 160, 0, 1000000000, 0);
            AddLabel(settings, "0 = 不限；多点时遍历一遍为一轮", 346, 259, 333, 35, 9, Muted, false);

            locationMode = Choice(this, "点击位置模式", 30, 676, new[] { "跟随鼠标", "固定单点", "多点循环", "动作宏" });
            locationMode.Width = 178;
            position = AddLabel(this, "跟随鼠标 · F7 记录单点", 225, 679, 495, 35, 10, Muted, false);
            locationMode.SelectedIndexChanged += delegate { UpdatePosition(); SelectEditorTab(); };

            positionsPanel = Card(743, 180, 527, 663, Color.White);
            AddLabel(positionsPanel, "循环位置", 23, 19, 338, 38, 13, Ink, true);
            AddLabel(positionsPanel, "F7 添加 · 左键蓝 / 右键绿 / 中键红 · 仅点击勾选项", 23, 60, 488, 30, 9, Muted, false);
            // Left column: the point list, then the per-point repeat settings beneath it.
            positionsList = new ListView { AccessibleName = "循环点击位置列表", Bounds = new Rectangle(23, 94, 254, 100), CheckBoxes = true, View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, MultiSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, ShowItemToolTips = true, Font = UiFont(9, FontStyle.Regular) };
            positionsList.Columns.Add("位置", 160);
            positionsList.SizeChanged += delegate { positionsList.Columns[0].Width = Math.Max(100, positionsList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 3); };
            positionsPanel.Controls.Add(positionsList);
            positionsList.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                // The list replays check states while the form is first shown (false then
                // true for each row), so reconcile once after the burst instead of
                // treating every notification as an edit.
                if (updatingPositions || loadingPreferences || !IsHandleCreated) return;
                BeginInvoke(new Action(SyncPositionChecked));
            };
            positionsList.SelectedIndexChanged += delegate { UpdatePointButton(); };
            // Manual row reordering. OLE drag-drop (AllowDrop + DoDragDrop) calls
            // RegisterDragDrop, which can fail when the app runs elevated or in a
            // restricted session; that exception used to crash the window before it
            // was even shown.
            positionsList.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                reorderFrom = -1;
                reorderArmed = false;
                if (args.Button != MouseButtons.Left) return;
                ListViewItem item = positionsList.GetItemAt(args.X, args.Y);
                if (item == null) return;
                reorderFrom = item.Index;
                reorderStart = args.Location;
                reorderArmed = true;
                positionsList.Capture = true;
            };
            positionsList.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!reorderArmed) return;
                if (Math.Abs(args.X - reorderStart.X) < 4 && Math.Abs(args.Y - reorderStart.Y) < 4) return;
                positionsList.Cursor = Cursors.SizeAll;
            };
            positionsList.MouseUp += delegate(object sender, MouseEventArgs args)
            {
                positionsList.Capture = false;
                positionsList.Cursor = Cursors.Default;
                bool armed = reorderArmed;
                int from = reorderFrom;
                reorderArmed = false;
                reorderFrom = -1;
                if (!armed || from < 0 || engine.Running || pending) return;
                ListViewItem target = positionsList.GetItemAt(args.X, args.Y);
                if (target == null || target.Index == from) return;
                PositionEntry entry = positions[from];
                positions.RemoveAt(from);
                int to = target.Index;
                if (to > from) to--;
                if (args.Y > target.Bounds.Top + target.Bounds.Height / 2) to++;
                if (to < 0) to = 0;
                if (to > positions.Count) to = positions.Count;
                positions.Insert(to, entry);
                RefreshPositions(to);
            };
            AddLabel(positionsPanel, "每点动作次数", 23, 198, 254, 29, 9, Muted, false);
            pointCount = Number(positionsPanel, "每点动作次数", 23, 224, 254, 1, 10000, 1);
            AddLabel(positionsPanel, "点内间隔（毫秒）", 23, 268, 254, 29, 9, Muted, false);
            pointRepeatInterval = Number(positionsPanel, "点内重复间隔", 23, 294, 254, 0, 3600000, 100);
            AddLabel(positionsPanel, "此点点击方式", 23, 338, 254, 26, 9, Muted, false);
            pointMode = Choice(positionsPanel, "选中点的点击方式", 23, 364, new[] { "沿用全局", "单击", "双击" });
            pointMode.Width = 254;
            pointMode.Enabled = false;
            pointMode.SelectedIndexChanged += delegate
            {
                int index = SelectedPosition;
                if (updatingPointButton || index < 0) return;
                positions[index].DoubleClick = pointMode.SelectedIndex - 1;
                if (index < positionsList.Items.Count) positionsList.Items[index].ToolTipText = PointDescription(index);
            };
            AddLabel(positionsPanel, "备注", 23, 408, 254, 26, 9, Muted, false);
            pointLabel = new TextBox { Bounds = new Rectangle(23, 434, 254, 30), MaxLength = 40, Font = UiFont(9, FontStyle.Regular), BorderStyle = BorderStyle.FixedSingle, Enabled = false };
            pointLabel.TextChanged += delegate { UpdatePointLabel(); };
            positionsPanel.Controls.Add(pointLabel);
            inheritInterval = new CheckBox { Text = "后延时沿用全局间隔", Checked = true, Bounds = new Rectangle(23, 474, 254, 30), Font = UiFont(9, FontStyle.Regular) };
            positionsPanel.Controls.Add(inheritInterval);
            
            // Right column: the selected point's coordinates, button and delays.
            AddLabel(positionsPanel, "X", 306, 100, 34, 33, 10, Muted, false);
            pointX = Number(positionsPanel, "选中点 X 坐标", 340, 94, 187, -1000000, 1000000, 0);
            AddLabel(positionsPanel, "Y", 306, 145, 34, 33, 10, Muted, false);
            pointY = Number(positionsPanel, "选中点 Y 坐标", 340, 139, 187, -1000000, 1000000, 0);
            AddLabel(positionsPanel, "选中点按键", 306, 188, 221, 30, 9, Muted, false);
            pointButton = Choice(positionsPanel, "选中点击点的鼠标按键", 306, 214, new[] { "左键 · 蓝色", "右键 · 绿色", "中键 · 红色" });
            pointButton.Width = 221;
            pointButton.Enabled = false;
            pointButton.SelectedIndexChanged += delegate
            {
                int index = SelectedPosition;
                if (updatingPointButton || index < 0) return;
                positions[index].Button = pointButton.SelectedIndex;
                RefreshPositions(index);
            };
            AddLabel(positionsPanel, "点击前等待（毫秒）", 306, 254, 221, 30, 9, Muted, false);
            delayBefore = Number(positionsPanel, "选中点点击前延时", 306, 278, 221, 0, 3600000, 0);
            AddLabel(positionsPanel, "点击后等待（毫秒）", 306, 322, 221, 30, 9, Muted, false);
            delayAfter = Number(positionsPanel, "选中点点击后延时", 306, 346, 221, 0, 3600000, 100);
            applyCoordinates = SmallButton(positionsPanel, "应用坐标", 306, 411, 221, delegate { ApplyCoordinates(); });
            editPoints = SmallButton(positionsPanel, "锁定标记（穿透）", 306, 453, 221, delegate { TogglePointEditing(); });
            delayBefore.ValueChanged += delegate { UpdatePointDelays(); };
            delayAfter.ValueChanged += delegate { UpdatePointDelays(); };
            inheritInterval.CheckedChanged += delegate { UpdatePointDelays(); };
            interval.ValueChanged += delegate { if (inheritInterval.Checked) UpdatePointButton(); };
            pointCount.ValueChanged += delegate { UpdatePointDelays(); };
            pointRepeatInterval.ValueChanged += delegate { UpdatePointDelays(); };
            // Bottom action row, full width under both columns.
            RouteButton("添加位置（3 秒后取点）", 23, 516, 482, delegate { BeginCapture(); });
            RouteButton("上移", 23, 568, 105, delegate { MovePosition(-1); });
            RouteButton("下移", 138, 568, 105, delegate { MovePosition(1); });
            RouteButton("删除", 253, 568, 118, delegate { DeleteSelectedPositions(); });
            RouteButton("清空", 380, 568, 125, delegate
            {
                if (positions.Count > 0 && ShowModal(delegate { return MessageBox.Show(this, "清空当前方案中的全部点位？", "清空点位", MessageBoxButtons.YesNo, MessageBoxIcon.Question); }) == DialogResult.Yes)
                { positions.Clear(); RefreshPositions(-1); }
            });

            start = ActionButton("开始连点   F6", 30, 733, 445, Green, Color.White);
            stopButton = ActionButton("停止   F8", 493, 733, 228, Color.FromArgb(226, 233, 227), Ink);
            start.Click += delegate { Toggle(); };
            stopButton.Click += delegate { StopRun("已停止"); };
            // The window can be stretched or shown full screen; this button mirrors F11.
            // The frame is 18 px per side after DPI scaling, so this stays inside the form.
            fullScreenButton = SmallButton(this, "全屏   F11", 1145, 40, 160, delegate { ToggleFullScreen(); });
            fullScreenButton.Font = UiFont(11, FontStyle.Regular);
            hint = AddLabel(this, "启动前倒数 3 秒 · 鼠标移至主屏左上角可紧急停止", 31, 807, 688, 46, 9, Muted, false);
            InitializeAdvanced();
            hintDefaultText = hint.Text;
            hintDefaultColor = hint.ForeColor;
            LoadProfiles();
            CaptureBaseline();
            HookDirty(this);
            // Moving or resizing the window can uncover or cover a point marker.
            Move += delegate { markersDirty = true; };
            Resize += delegate { markersDirty = true; };
            timer.Interval = 80;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        private Panel Card(int x, int y, int w, int h, Color color)
        {
            Panel card = new Panel { Bounds = new Rectangle(x, y, w, h), BackColor = color };
            Controls.Add(card);
            return card;
        }

        // ------------------------------------------------------------------
        // Window stretching / full screen
        // ------------------------------------------------------------------
        // The layout is designed for 1325x880. Larger windows are handled by one simple
        // transform, applied only to the containers that can absorb space without
        // disturbing any control's position inside them:
        //   * both main columns grow taller,
        //   * the point/step lists inside the editors grow taller with them,
        //   * the action row and the status lines move down to the new bottom edge.
        // Everything else keeps its design position, so no control can collide or drift.

        private void CaptureBaseline()
        {
            layoutReady = true;
            // The zoom only grows, so this floor never fights it; it just keeps a user
            // resize from clipping the design canvas.
            MinimumSize = new Size(DesignWidth + WindowChromeWidth, DesignHeight + WindowChromeHeight);
            Resize += OnFormResize;
        }

        // The layout is designed for 1325x873. A larger window zooms the whole interface
        // proportionally: WinForms scales control bounds and fonts together, and scaling
        // back restores the design geometry exactly. So nothing is left floating in empty
        // space, and nothing can collide because nothing is repositioned independently.
        private void ApplyWindowLayout()
        {
            if (!layoutReady || applyingScale) return;
            // While a mode change (full screen or the return to the design size) is in
            // progress the target is explicit; a user resize derives it from the window.
            float target = resizing ? DesiredScale() : requestedScale;
            if (Math.Abs(target - currentScale) < 0.01f) return;
            applyingScale = true;
            Resize -= OnFormResize;                 // our own resize must not re-enter
            SuspendLayout();
            try
            {
                Scale(new SizeF(target / currentScale, target / currentScale));
                currentScale = target;
                requestedScale = target;
                // Scale() also changes the window size, so set the exact rect last. This
                // resize is suppressed above, otherwise it would zoom again and run away.
                Bounds = new Rectangle(Left, Top,
                                       (int)Math.Round(DesignWidth * target) + WindowChromeWidth,
                                       (int)Math.Round(DesignHeight * target) + WindowChromeHeight);
            }
            finally
            {
                ResumeLayout(true);
                Resize += OnFormResize;
                applyingScale = false;
            }
        }

        // How far the interface zooms for this window. Taken from the window size the zoom
        // itself would produce, so the result is a stable fixed point: applying it lands on
        // exactly the size the next call measures, and the zoom cannot run away.
        private float DesiredScale()
        {
            float fit = Math.Min(ClientSize.Width / (float)DesignWidth,
                                 ClientSize.Height / (float)DesignHeight);
            if (fit < 1f) return 1f;
            return Math.Min(MaximumUiScale, fit);
        }

        // The largest zoom the current screen could ever need, used as the explicit target
        // when the user asks for full screen.
        private float FitScale()
        {
            Rectangle area = fullScreen ? Screen.FromControl(this).Bounds
                                        : Screen.FromControl(this).WorkingArea;
            float fitW = Math.Max(DesignWidth, area.Width - WindowChromeWidth) / (float)DesignWidth;
            float fitH = Math.Max(DesignHeight, area.Height - WindowChromeHeight) / (float)DesignHeight;
            if (fitW < 1f) fitW = 1f;
            if (fitH < 1f) fitH = 1f;
            return Math.Min(MaximumUiScale, Math.Min(fitW, fitH));
        }

        private void OnFormResize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized) return;
            // A genuine user resize: recompute the zoom from the window the user dragged.
            resizing = true;
            try { ApplyWindowLayout(); }
            finally { resizing = false; }
        }
        private void ToggleFullScreen()
        {
            fullScreen = !fullScreen;
            Resize -= OnFormResize;                 // both branches set the window rect
            applyingScale = true;
            SuspendLayout();
            try
            {
                if (fullScreen)
                {
                    restoreBounds = Bounds;
                    restoreTopMost = TopMost;
                    // Undo any zoom while the window still has a border, so the frame
                    // measurement below is the borderless one.
                    if (Math.Abs(currentScale - 1f) >= 0.01f)
                    {
                        Scale(new SizeF(1f / currentScale, 1f / currentScale));
                        currentScale = 1f;
                    }
                    FormBorderStyle = FormBorderStyle.None;
                    TopMost = true;
                    WindowState = FormWindowState.Normal;
                    Bounds = Screen.FromControl(this).WorkingArea;
                    // Explicit target: fill the screen. ApplyWindowLayout below applies it.
                    requestedScale = FitScale();
                }
                else
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                    // Undo the zoom now that the border is back; ApplyWindowLayout below
                    // then sizes the window for the design canvas.
                    if (Math.Abs(currentScale - 1f) >= 0.01f)
                    {
                        Scale(new SizeF(1f / currentScale, 1f / currentScale));
                        currentScale = 1f;
                    }
                    TopMost = restoreTopMost;
                    WindowState = FormWindowState.Normal;
                    // Return to exactly the window the user had, and keep the zoom
                    // consistent with that size so the next resize starts from the truth.
                    Bounds = restoreBounds;
                    requestedScale = Math.Min(MaximumUiScale,
                        Math.Max(1f, Math.Min(restoreBounds.Width / (float)DesignWidth,
                                               restoreBounds.Height / (float)DesignHeight)));
                    if (Math.Abs(requestedScale - currentScale) < 0.01f) requestedScale = currentScale;
                }
            }
            finally
            {
                ResumeLayout(true);
                Resize += OnFormResize;
                applyingScale = false;
            }
            if (fullScreenButton != null) fullScreenButton.Text = fullScreen ? "退出全屏" : "全屏   F11";
            if (hint != null)
            {
                hint.Text = fullScreen ? "F11 或点击按钮退出全屏 · 鼠标移至主屏左上角可紧急停止" : hintDefaultText;
                hint.ForeColor = hintDefaultColor;
            }
            ApplyWindowLayout();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.F11 && !modalOpen)
            {
                ToggleFullScreen();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        internal static Label AddLabel(Control parent, string text, int x, int y, int w, int h, float size, Color color, bool bold)
        {
            Label label = new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = UiFont(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true };
            parent.Controls.Add(label);
            return label;
        }

        internal static NumericUpDown Number(Control parent, string name, int x, int y, int width, decimal min, decimal max, decimal value)
        {
            NumericUpDown input = new NumericUpDown { AccessibleName = name, Bounds = new Rectangle(x, y, width, 40), Minimum = min, Maximum = max, Value = value, Font = UiFont(12, FontStyle.Regular), BorderStyle = BorderStyle.FixedSingle, ThousandsSeparator = true };
            parent.Controls.Add(input);
            return input;
        }

        internal static ComboBox Choice(Control parent, string name, int x, int y, string[] values)
        {
            ComboBox choice = new ComboBox { AccessibleName = name, Bounds = new Rectangle(x, y, 298, 40), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(244, 247, 244), Font = UiFont(11, FontStyle.Regular) };
            choice.Items.AddRange(values);
            choice.SelectedIndex = 0;
            parent.Controls.Add(choice);
            return choice;
        }

        private Button ActionButton(string text, int x, int y, int width, Color back, Color fore)
        {
            Button action = new Button { Text = text, Bounds = new Rectangle(x, y, width, 61), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Cursor = Cursors.Hand, Font = UiFont(12, FontStyle.Bold), UseVisualStyleBackColor = false };
            action.FlatAppearance.BorderSize = 0;
            Controls.Add(action);
            return action;
        }

        private void RouteButton(string text, int x, int y, int width, EventHandler handler)
        {
            SmallButton(positionsPanel, text, x, y, width, handler);
        }

        internal static Button SmallButton(Control parent, string text, int x, int y, int width, EventHandler handler)
        {
            Button action = new Button { Text = text, Bounds = new Rectangle(x, y, width, 42), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(237, 243, 238), Cursor = Cursors.Hand };
            action.FlatAppearance.BorderSize = 0;
            action.Click += handler;
            parent.Controls.Add(action);
            return action;
        }

        private void RefreshPositions(int selected)
        {
            MarkDirty();
            updatingPositions = true;
            positionsList.BeginUpdate();
            positionsList.Items.Clear();
            ImageList previousImages = badgeImages;
            badgeImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(MarkerSize, MarkerSize) };
            positionsList.SmallImageList = badgeImages;
            badgeCache.BeginRefresh();
            for (int i = 0; i < positions.Count; i++)
            {
                PositionEntry entry = positions[i];
                // Cached badges: an edit no longer allocates one bitmap per row.
                badgeImages.Images.Add(badgeCache.Get(i + 1, entry.Button, MarkerSize));
                positionsList.Items.Add(new ListViewItem(PointTitle(i), i) { Checked = entry.Enabled, ToolTipText = PointDescription(i) });
            }
            if (previousImages != null) previousImages.Dispose();
            badgeCache.Trim();
            if (selected >= 0 && selected < positions.Count)
            {
                positionsList.Items[selected].Selected = true;
                positionsList.Items[selected].Focused = true;
                positionsList.EnsureVisible(selected);
            }
            positionsList.EndUpdate();
            updatingPositions = false;
            UpdatePointButton();
            UpdatePosition();
        }

        private void DeleteSelectedPositions()
        {
            if (positionsList.SelectedIndices.Count == 0) return;
            List<int> selected = new List<int>();
            foreach (int index in positionsList.SelectedIndices) selected.Add(index);
            selected.Sort();
            for (int i = selected.Count - 1; i >= 0; i--) positions.RemoveAt(selected[i]);
            RefreshPositions(Math.Min(selected[0], positions.Count - 1));
        }

        private string PointTitle(int index)
        {
            PositionEntry point = positions[index];
            string name = point.Label.Length == 0 ? "" : point.Label + " · ";
            return name + PointBadge.ButtonName(point.Button) + "   X " + point.X + "   Y " + point.Y;
        }

        private int SelectedPosition { get { return positionsList.SelectedIndices.Count == 0 ? -1 : positionsList.SelectedIndices[0]; } }
        private int MarkerSize { get { return Math.Max(28, Math.Min(96, (int)Math.Round(32 * AutoScaleFactorForMarkers))); } }
        private float AutoScaleFactorForMarkers
        {
            get { using (Graphics graphics = CreateGraphics()) return graphics.DpiX / 96f; }
        }
        private void UpdatePointButton()
        {
            if (pointButton == null || updatingPositions) return;
            int index = SelectedPosition;
            updatingPointButton = true;
            pointButton.Enabled = index >= 0;
            pointMode.Enabled = pointLabel.Enabled = index >= 0;
            pointX.Enabled = pointY.Enabled = delayBefore.Enabled = inheritInterval.Enabled = applyCoordinates.Enabled = index >= 0;
            if (index >= 0)
            {
                PositionEntry point = positions[index];
                pointButton.SelectedIndex = point.Button;
                pointMode.SelectedIndex = point.DoubleClick + 1;
                pointLabel.Text = point.Label;
                pointX.Value = point.X;
                pointY.Value = point.Y;
                delayBefore.Value = point.DelayBefore;
                inheritInterval.Checked = point.DelayAfter < 0;
                delayAfter.Value = point.DelayAfter < 0 ? interval.Value : point.DelayAfter;
                if (pointCount != null) { pointCount.Value = point.Count; pointRepeatInterval.Value = point.RepeatInterval; }
            }
            delayAfter.Enabled = index >= 0 && !inheritInterval.Checked;
            if (pointCount != null) pointCount.Enabled = pointRepeatInterval.Enabled = index >= 0;
            updatingPointButton = false;
        }

        private void UpdatePointLabel()
        {
            int index = SelectedPosition;
            if (updatingPointButton || updatingPositions || index < 0) return;
            positions[index].Label = (pointLabel.Text ?? "").Trim();
            if (index < positionsList.Items.Count)
            {
                positionsList.Items[index].Text = PointTitle(index);
                positionsList.Items[index].ToolTipText = PointDescription(index);
            }
        }

        private void UpdatePointDelays()
        {
            int index = SelectedPosition;
            if (updatingPointButton || updatingPositions || index < 0) return;
            positions[index].DelayBefore = (int)delayBefore.Value;
            positions[index].DelayAfter = inheritInterval.Checked ? -1 : (int)delayAfter.Value;
            if (pointCount != null) { positions[index].Count = (int)pointCount.Value; positions[index].RepeatInterval = (int)pointRepeatInterval.Value; }
            delayAfter.Enabled = !inheritInterval.Checked;
            if (inheritInterval.Checked)
            {
                updatingPointButton = true;
                delayAfter.Value = interval.Value;
                updatingPointButton = false;
            }
            positionsList.Items[index].ToolTipText = PointDescription(index);
        }

        private string PointDescription(int index)
        {
            PositionEntry point = positions[index];
            string click = point.DoubleClick < 0 ? "沿用全局" : point.DoubleClick == 1 ? "双击" : "单击";
            return (point.Label.Length == 0 ? "" : "备注：" + point.Label + "\n") +
                "位置 " + (index + 1) + " · " + PointBadge.ButtonName(point.Button) + " · " + click + " · X " + point.X + " / Y " + point.Y +
                "\n点击前 " + point.DelayBefore + " ms；点击后 " + (point.DelayAfter < 0 ? "沿用全局间隔" : point.DelayAfter + " ms") +
                "\n重复 " + point.Count + " 次；点内间隔 " + point.RepeatInterval + " ms";
        }

        private void ApplyCoordinates()
        {
            int index = SelectedPosition;
            if (index < 0) return;
            Point target = new Point((int)pointX.Value, (int)pointY.Value);
            string error = PositionError(StoredToScreen(target));
            if (error != null) { status.Text = "●  " + error; return; }
            positions[index].X = target.X;
            positions[index].Y = target.Y;
            RefreshPositions(index);
            status.Text = "●  已更新位置 " + (index + 1);
        }

        private void TogglePointEditing()
        {
            SetPointEditing(!editingPoints);
            if (editingPoints) showMarkers.Checked = true;
            status.Text = editingPoints ? "●  停止时可直接用左键拖动屏幕圆点" : "●  标记已锁定，鼠标点击可穿透";
        }

        private void SetPointEditing(bool value)
        {
            editingPoints = value;
            if (dragMarkers != null && dragMarkers.Checked != value) { dragMarkers.Checked = value; return; }
            if (editPoints != null) editPoints.Text = value ? "锁定标记（穿透）" : "允许拖动标记";
            SyncMarkers();
        }

        private bool CanDragMarkers
        {
            get { return editingPoints && !uiBusy && !pending && !engine.Running && !capturing && !targetCapturing && !modalOpen; }
        }

        private void MarkerMoved(int number, Point target)
        {
            if (!CanDragMarkers) { SyncMarkers(); return; }
            string error = PositionError(target);
            if (error != null) { status.Text = "●  " + error + "，已还原点位"; SyncMarkers(); return; }
            Point stored = ScreenToStored(target);
            if (locationMode.SelectedIndex == 2 && number > 0 && number <= positions.Count)
            {
                positions[number - 1].X = stored.X;
                positions[number - 1].Y = stored.Y;
                RefreshPositions(number - 1);
            }
            else if (locationMode.SelectedIndex == 1) { selectedPoint = stored; UpdatePosition(); }
            else if (locationMode.SelectedIndex == 3 && number > 0 && number <= macroEntries.Count && macroEntries[number - 1].Kind == ActionKind.Click)
            {
                macroEntries[number - 1].X = stored.X;
                macroEntries[number - 1].Y = stored.Y;
                RefreshMacro(number - 1);
            }
            status.Text = "●  位置 " + number + " 已移至 X " + stored.X + " / Y " + stored.Y;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            markersReady = true;
            SyncMarkers();
            if (advanced.StartMinimized) BeginInvoke(new Action(delegate { WindowState = FormWindowState.Minimized; }));
        }

        // Markers float above everything, including the clicker's own window. A point
        // that lands under the window would cover its text and controls, so it is hidden
        // until the window moves away.
        private bool MarkerFitsInDesktop(Point point, int size)
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return true;
            return !new Rectangle(point.X - size / 2, point.Y - size / 2, size, size).IntersectsWith(Bounds);
        }

        // Screen origin of the bound target's client area, or false when unavailable.
        private bool TargetOrigin(out Point origin)
        {
            origin = Point.Empty;
            if (advanced == null) return false;
            IntPtr window = targetWindow;
            if (window == IntPtr.Zero || !Native.IsWindow(window))
                window = Native.FindTarget(advanced.TargetTitle, advanced.TargetProcess);
            if (window == IntPtr.Zero || window == Handle) return false;
            if (!Native.ClientOrigin(window, out origin)) return false;
            targetWindow = window;
            return true;
        }

        private Point StoredToScreen(Point stored)
        {
            if (!advanced.RelativeCoordinates) return stored;
            Point origin;
            return TargetOrigin(out origin) ? new Point(origin.X + stored.X, origin.Y + stored.Y) : stored;
        }

        private Point ScreenToStored(Point screen)
        {
            if (!advanced.RelativeCoordinates) return screen;
            Point origin;
            return TargetOrigin(out origin) ? new Point(screen.X - origin.X, screen.Y - origin.Y) : screen;
        }

        private void SyncMarkers()
        {
            if (!markersReady || IsDisposed || Disposing) return;
            int size = MarkerSize;
            bool relative = advanced.RelativeCoordinates;
            Point origin = Point.Empty;
            // Without a target window there is no correct screen position to draw at.
            bool markersUsable = !relative || TargetOrigin(out origin);
            markerLayer.BeginUpdate();
            if (showMarkers.Checked && markersUsable)
            {
                if (locationMode.SelectedIndex == 2)
                {
                    for (int i = 0; i < positions.Count; i++)
                    {
                        Point screen = relative ? new Point(origin.X + positions[i].X, origin.Y + positions[i].Y) : new Point(positions[i].X, positions[i].Y);
                        if (positions[i].Enabled && MarkerFitsInDesktop(screen, size))
                            markerLayer.Add(screen, i + 1, positions[i].Button, size, CanDragMarkers, MarkerMoved);
                    }
                }
                else if (locationMode.SelectedIndex == 1 && selectedPoint.HasValue)
                {
                    Point screen = relative ? new Point(origin.X + selectedPoint.Value.X, origin.Y + selectedPoint.Value.Y) : selectedPoint.Value;
                    if (MarkerFitsInDesktop(screen, size))
                        markerLayer.Add(screen, 1, button.SelectedIndex, size, CanDragMarkers, MarkerMoved);
                }
                else if (locationMode.SelectedIndex == 3)
                {
                    for (int i = 0; i < macroEntries.Count; i++)
                    {
                        if (!macroEntries[i].Enabled || macroEntries[i].Kind != ActionKind.Click) continue;
                        Point screen = relative ? new Point(origin.X + macroEntries[i].X, origin.Y + macroEntries[i].Y) : new Point(macroEntries[i].X, macroEntries[i].Y);
                        if (MarkerFitsInDesktop(screen, size))
                            markerLayer.Add(screen, i + 1, macroEntries[i].Button, size, CanDragMarkers, MarkerMoved);
                    }
                }
            }
            markerLayer.EndUpdate();
            markerLayer.Highlight(engine.Running ? engine.SourceNumber : 0);
        }

        private void MovePosition(int delta)
        {
            int index = SelectedPosition;
            int next = index + delta;
            if (index < 0 || next < 0 || next >= positions.Count) return;
            PositionEntry entry = positions[index];
            positions.RemoveAt(index);
            positions.Insert(next, entry);
            RefreshPositions(next);
        }

        private void BeginCapture()
        {
            if (pending || engine.Running || capturing || targetCapturing) return;
            locationMode.SelectedIndex = 2;
            capturing = true;
            countdown.Restart();
            SetBusy(true);
            status.Text = "●  3 秒后取点，请移至目标位置";
        }

        private void CapturePosition()
        {
            Point point = ScreenToStored(Native.CursorPoint());
            string error = PositionError(StoredToScreen(point));
            if (error != null) { status.Text = "●  " + error; return; }
            if (locationMode.SelectedIndex == 3)
            {
                if (macroEntries.Count >= ProfileStore.MaxPoints) { status.Text = "●  最多 1000 个步骤"; return; }
                macroEntries.Add(new MacroEntry { X = point.X, Y = point.Y, Button = button.SelectedIndex, DoubleClick = mode.SelectedIndex == 1, DelayAfter = (int)interval.Value });
                RefreshMacro(macroEntries.Count - 1);
                status.Text = "●  已添加点击步骤 " + macroEntries.Count;
            }
            else if (locationMode.SelectedIndex == 2)
            {
                if (positions.Count >= ProfileStore.MaxPoints) { status.Text = "●  每个方案最多 1000 个点位"; return; }
                positions.Add(new PositionEntry { X = point.X, Y = point.Y, Button = button.SelectedIndex });
                RefreshPositions(positions.Count - 1);
                status.Text = "●  已添加位置 " + positions.Count;
            }
            else
            {
                selectedPoint = point;
                locationMode.SelectedIndex = 1;
                UpdatePosition();
                status.Text = "●  已记录固定位置";
            }
        }

        private string PositionError(Point point)
        {
            bool onScreen = false;
            foreach (Screen screen in Screen.AllScreens) if (screen.Bounds.Contains(point)) onScreen = true;
            if (!onScreen) return "位置不在屏幕内，请重新取点";
            if (point.X >= 0 && point.X < 5 && point.Y >= 0 && point.Y < 5) return "左上角为紧急停止区，请重新取点";
            if (Native.IsOwnWindow(point, Handle)) return "请选择连点器窗口以外的位置";
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (useHotkeys) for (int id = 1; id <= 6; id++) Native.UnregisterHotKey(Handle, id);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            // Another launch asks this instance to come to the front instead of
            // showing "already running" and confusing the user.
            if (Native.ActivateMessage != 0 && message.Msg == (int)Native.ActivateMessage)
            {
                RestoreWindow();
                return;
            }
            if (message.Msg == Native.WM_HOTKEY)
            {
                if (modalOpen && message.WParam.ToInt32() != 3 && message.WParam.ToInt32() != 6) return;
                switch (message.WParam.ToInt32())
                {
                    case 1: Toggle(); break;
                    case 2:
                    case 5:
                        if (recorder.Recording) break;
                        if (!pending && !engine.Running && !targetCapturing)
                        {
                            capturing = false;
                            countdown.Stop();
                            SetBusy(false);
                            CapturePosition();
                        }
                        break;
                    case 3:
                        if (recorder.Recording) { StopRecording(); break; }
                        StopRun("已通过 " + HotkeyText(advanced.HotkeyStop) + " 停止");
                        break;
                    case 4: PauseRun(); break;
                    case 6:
                        if (recorder.Recording) StopRecording();
                        StopRun("已通过 " + HotkeyText(advanced.HotkeyEmergency) + " 紧急停止");
                        break;
                }
            }
            base.WndProc(ref message);
        }

        private void UpdatePosition()
        {
            if (locationMode.SelectedIndex == 3)
                position.Text = "宏步骤 " + macroEntries.FindAll(delegate(MacroEntry entry) { return entry.Enabled; }).Count + " / " + macroEntries.Count + " · F7 添加点击";
            else if (locationMode.SelectedIndex == 2)
            {
                int enabled = positions.FindAll(delegate(PositionEntry entry) { return entry.Enabled; }).Count;
                position.Text = "已勾选 " + enabled + " / " + positions.Count + " 个位置 · F7 继续添加";
            }
            else position.Text = locationMode.SelectedIndex == 1
                ? (selectedPoint.HasValue ? "X " + selectedPoint.Value.X + "   Y " + selectedPoint.Value.Y + "  ·  F7 重新取点" : "将鼠标移到目标处，按 F7 取点")
                : "跟随鼠标 · F7 记录单点";
            SyncMarkers();
        }

        private void Toggle()
        {
            if (pending || engine.Running || capturing || targetCapturing) { StopRun("已停止"); return; }
            if (recorder.Recording) { status.Text = "●  正在录制，请先按 " + HotkeyText(advanced.HotkeyStop) + " 停止录制"; return; }
            if (!hotkeysReady) return;
            UpdatePointDelays();
            // ValidateEditText runs when Value is read, including edits made just before a global hotkey.
            int milliseconds = (int)interval.Value;
            long times = (long)limit.Value;
            List<string> problems = new List<string>();
            List<string> warnings = new List<string>();
            List<Point> route = new List<Point>();
            List<int> routeButtons = new List<int>();
            List<int> before = new List<int>();
            List<int> after = new List<int>();
            List<int> repetitions = new List<int>(), repetitionIntervals = new List<int>(), numbers = new List<int>(), doubleClicks = new List<int>();
            if (advanced.RelativeCoordinates)
            {
                Point relativeTest;
                if (!TargetOrigin(out relativeTest)) problems.Add("坐标是相对目标窗口的，但目标窗口未绑定或已关闭，请先绑定目标窗口。");
            }
            if (locationMode.SelectedIndex == 1)
            {
                if (!selectedPoint.HasValue) problems.Add("还没有记录点击位置，请先按 F7 取点。");
                else
                {
                    string error = PositionError(StoredToScreen(selectedPoint.Value));
                    if (error != null) problems.Add("固定位置：" + error + "。");
                }
            }
            if (locationMode.SelectedIndex == 2)
            {
                for (int i = 0; i < positions.Count; i++)
                {
                    if (!positions[i].Enabled) continue;
                    Point storedPoint = new Point(positions[i].X, positions[i].Y);
                    string error = PositionError(StoredToScreen(storedPoint));
                    if (error != null) { problems.Add("位置 " + (i + 1) + "：" + error + "。"); continue; }
                    route.Add(storedPoint);
                    routeButtons.Add(positions[i].Button);
                    before.Add(positions[i].DelayBefore);
                    after.Add(positions[i].DelayAfter < 0 ? milliseconds : positions[i].DelayAfter);
                    repetitions.Add(positions[i].Count);
                    repetitionIntervals.Add(positions[i].RepeatInterval);
                    doubleClicks.Add(positions[i].DoubleClick);
                    numbers.Add(i + 1);
                }
                if (route.Count == 0) problems.Add("请先添加并勾选至少一个循环位置。");
            }
            ClickOptions options = new ClickOptions { Interval = milliseconds, Button = button.SelectedIndex, DoubleClick = mode.SelectedIndex == 1, HighPrecision = advanced.HighPrecision, Limit = times, JitterInterval = advanced.JitterInterval, JitterPosition = advanced.JitterPosition, TimeLimitMs = advanced.RunMinutes > 0 ? advanced.RunMinutes * 60000L : 0, RelativeToTarget = advanced.RelativeCoordinates, FixedPoint = locationMode.SelectedIndex == 1 ? selectedPoint : null, Positions = locationMode.SelectedIndex == 2 ? route.ToArray() : null, PositionButtons = locationMode.SelectedIndex == 2 ? routeButtons.ToArray() : null, DelaysBefore = locationMode.SelectedIndex == 2 ? before.ToArray() : null, DelaysAfter = locationMode.SelectedIndex == 2 ? after.ToArray() : null, Desktop = SystemInformation.VirtualScreen, MainWindow = Handle };
            if (locationMode.SelectedIndex == 2)
            {
                options.RepeatCounts = repetitions.ToArray(); options.RepeatIntervals = repetitionIntervals.ToArray();
                options.SourceNumbers = numbers.ToArray(); options.PositionDoubleClicks = doubleClicks.ToArray();
            }
            PrepareAdvancedRun(options, problems);
            if (milliseconds < 30)
                warnings.Add(advanced.HighPrecision
                    ? "间隔 " + milliseconds + " 毫秒已接近 Windows 计时精度的极限，实测速度可能低于设定值。"
                    : "间隔 " + milliseconds + " 毫秒，但「高精度计时」未开启，实际间隔通常不低于 15 毫秒。");
            if (problems.Count > 0) { ShowCheck("开始前的检查", "先修正下面这些问题：", problems, false); return; }
            if (warnings.Count > 0 && !ShowCheck("开始前的检查", "没有问题，但请留意：", warnings, true)) return;
            if (advanced.ScheduleEnabled)
            {
                DateTime now = DateTime.Now;
                DateTime target = new DateTime(now.Year, now.Month, now.Day, Math.Max(0, advanced.ScheduleHour), Math.Max(0, advanced.ScheduleMinute), 0);
                if (target <= now) target = target.AddDays(1);
                scheduledStart = target;
                pendingOptions = options;
                pending = true;
                SetBusy(true);
                status.Text = "●  定时开始：" + target.ToString("MM-dd HH:mm") + "，按 " + HotkeyText(advanced.HotkeyStart) + " 取消";
                return;
            }
            pendingOptions = options;
            pending = true;
            countdown.Restart();
            SetBusy(true);
            status.Text = "●  " + advanced.StartupDelay + " 秒后开始，请移至目标位置";
            if (advanced.StartupDelay == 0) StartPendingRun();
        }

        // One dialog for every start problem, instead of fixing them one failed start
        // at a time. Warnings can be accepted; blocking problems cannot.
        private bool ShowCheck(string title, string intro, List<string> lines, bool allowContinue)
        {
            using (Form dialog = new Form
            {
                Text = title, ClientSize = new Size(620, 430), Font = UiFont(9.5F, FontStyle.Regular),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                AddLabel(dialog, intro, 23, 20, 574, 40, 10, Ink, false);
                ListBox list = new ListBox { Bounds = new Rectangle(23, 66, 574, 280), Font = UiFont(9.5F, FontStyle.Regular), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
                list.Items.AddRange(lines.ToArray());
                dialog.Controls.Add(list);
                Button primary = SmallButton(dialog, allowContinue ? "仍然开始" : "关闭", 330, 360, 130, delegate { });
                primary.DialogResult = allowContinue ? DialogResult.OK : DialogResult.Cancel;
                dialog.AcceptButton = primary;
                if (allowContinue)
                {
                    Button cancel = SmallButton(dialog, "取消", 470, 360, 120, delegate { });
                    cancel.DialogResult = DialogResult.Cancel;
                    dialog.CancelButton = cancel;
                }
                return ShowModal(delegate { return dialog.ShowDialog(this); }) == DialogResult.OK;
            }
        }

        private void SetBusy(bool busy)
        {
            uiBusy = busy;
            settings.Enabled = !busy;
            locationMode.Enabled = !busy;
            positionsPanel.Enabled = !busy;
            profilePanel.Enabled = !busy;
            SetAdvancedBusy(busy);
            dragMarkers.Enabled = !busy;
            start.Text = capturing ? "取消取点   " + HotkeyText(advanced.HotkeyStart) : busy ? "停止连点   " + HotkeyText(advanced.HotkeyStart) : "开始连点   " + HotkeyText(advanced.HotkeyStart);
            SyncMarkers();
        }

        private void StopRun(string reason)
        {
            bool active = engine.Running || pending || capturing || targetCapturing;
            scheduledStart = DateTime.MinValue;
            pending = false;
            capturing = false;
            targetCapturing = false;
            countdown.Stop();
            engine.Stop(reason);
            if (active) Log.Info("停止：" + reason);
            count.Text = engine.Rounds.ToString("N0");
            wasRunning = false;
            SetBusy(false);
            status.Text = "●  " + reason;
            UpdateRateText();
            UpdateRunDisplay();
        }

        private void Tick()
        {
            if (advanced.RelativeCoordinates)
            {
                Point currentOrigin;
                bool haveOrigin = TargetOrigin(out currentOrigin);
                if (haveOrigin != relativeOriginKnown || (haveOrigin && currentOrigin != relativeOrigin))
                {
                    relativeOriginKnown = haveOrigin;
                    relativeOrigin = currentOrigin;
                    markersDirty = true;
                }
            }
            if (markersDirty) { markersDirty = false; SyncMarkers(); }
            AutoSaveIfDue();
            if (TickTargetCapture()) return;
            if (recorder.Recording)
            {
                status.Text = "●  录制中 · 已记录 " + recorder.Count + " 个动作 · " + HotkeyText(advanced.HotkeyStop) + " 停止";
                UpdateRunDisplay();
                return;
            }
            if (pending && scheduledStart != DateTime.MinValue)
            {
                TimeSpan left = scheduledStart - DateTime.Now;
                if (left <= TimeSpan.Zero)
                {
                    scheduledStart = DateTime.MinValue;
                    StartPendingRun();
                }
                else
                {
                    status.Text = "●  " + scheduledStart.ToString("HH:mm") + " 定时开始 · 还有 " +
                        ((int)left.TotalHours).ToString("00") + ":" + left.Minutes.ToString("00") + ":" + left.Seconds.ToString("00");
                    UpdateRunDisplay();
                    return;
                }
            }
            // A pause the guard caused is easy to miss when the window is hidden or
            // the user is working in the target app, so announce it once per pause.
            if (engine.Running && engine.Paused && engine.AutoPaused && !pauseNotified)
            { pauseNotified = true; Notify(engine.Reason); }
            if (!engine.Running || !engine.Paused) pauseNotified = false;
            if (capturing)
            {
                if (countdown.ElapsedMilliseconds >= 3000)
                {
                    capturing = false;
                    countdown.Stop();
                    SetBusy(false);
                    CapturePosition();
                }
                else status.Text = "●  " + (int)Math.Ceiling((3000 - countdown.ElapsedMilliseconds) / 1000.0) + " 秒后取点，请移至目标位置";
            }
            if (pending)
            {
                if (countdown.ElapsedMilliseconds >= advanced.StartupDelay * 1000)
                {
                    StartPendingRun();
                }
                else status.Text = "●  " + (int)Math.Ceiling((advanced.StartupDelay * 1000 - countdown.ElapsedMilliseconds) / 1000.0) + " 秒后开始，请移至目标位置";
            }
            if (engine.Running) status.Text = engine.Paused ? "●  " + engine.Reason : "●  运行中 · 第 " + engine.SourceNumber + " 项（" + engine.CurrentPosition + " / " + engine.StepCount + "）· " + HotkeyText(advanced.HotkeyPause) + " 暂停";
            else if (wasRunning)
            {
                wasRunning = false;
                lastReport = new RunReport
                {
                    Started = runStarted, Duration = engine.Elapsed, Rounds = engine.Rounds, Actions = engine.Actions,
                    Reason = engine.Reason, Profile = CurrentProfile.Name, Mode = locationMode.Text
                };
                status.Text = "●  " + engine.Reason + " · " + lastReport.Rounds.ToString("N0") + " 轮 · 用时 " + RunReport.FormatDuration(lastReport.Duration);
                Log.Info("运行报告：" + lastReport.Summary);
                SetBusy(false);
                Notify("已结束：" + engine.Reason);
            }
            count.Text = engine.Rounds.ToString("N0");
            TimeSpan time = engine.Elapsed;
            elapsed.Text = ((long)time.TotalHours).ToString("00") + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
            UpdateRateText();
            UpdateRunDisplay();
        }

        // ------------------------------------------------------------------
        // Auto-save and run report
        // ------------------------------------------------------------------

        // Any user edit lands here; the UI timer writes the library back after a pause.
        private void MarkDirty()
        {
            // Programmatic model-to-control syncs are not edits.
            if (loadingPreferences || changingProfile || updatingPointButton || updatingPositions) return;
            dirty = true;
        }

        // Brings the model in line with the final check states after the list settles.
        private void SyncPositionChecked()
        {
            if (updatingPositions || loadingPreferences || IsDisposed) return;
            bool changed = false;
            int count = Math.Min(positions.Count, positionsList.Items.Count);
            for (int i = 0; i < count; i++)
            {
                if (positions[i].Enabled == positionsList.Items[i].Checked) continue;
                positions[i].Enabled = positionsList.Items[i].Checked;
                changed = true;
            }
            if (!changed) return;
            MarkDirty();
            UpdatePosition();
        }

        // Numeric fields, drop-downs, check boxes and text boxes all mark the profile
        // dirty through one recursive hook instead of per-control handlers.
        private void HookDirty(Control root)
        {
            foreach (Control child in root.Controls)
            {
                NumericUpDown number = child as NumericUpDown;
                if (number != null) number.ValueChanged += delegate { MarkDirty(); };
                ComboBox combo = child as ComboBox;
                if (combo != null) combo.SelectedIndexChanged += delegate { MarkDirty(); };
                CheckBox check = child as CheckBox;
                if (check != null) check.CheckedChanged += delegate { MarkDirty(); };
                TextBox text = child as TextBox;
                if (text != null) text.TextChanged += delegate { MarkDirty(); };
                if (child.Controls.Count > 0) HookDirty(child);
            }
        }

        private void AutoSaveIfDue()
        {
            if (!dirty || library == null || loadingPreferences || profileStoreBlocked) return;
            if (uiBusy || modalOpen || pending || capturing || targetCapturing || changingProfile || recorder.Recording || engine.Running) return;
            if ((DateTime.UtcNow - lastAutoSaveAttempt).TotalSeconds < AutoSaveSeconds) return;
            lastAutoSaveAttempt = DateTime.UtcNow;
            AutoSaveNow();
        }

        // Writes pending edits without the time gate; used by the timer and by tests.
        internal bool AutoSaveNow()
        {
            if (!dirty || library == null || loadingPreferences || profileStoreBlocked) return false;
            if (!SaveLibrary(EditedLibrary(), false)) return false;
            Log.Info("自动保存：" + CurrentProfile.Name);
            status.Text = "●  已自动保存 " + DateTime.Now.ToString("HH:mm:ss");
            return true;
        }

        private void ShowRunReport()
        {
            if (lastReport == null)
            {
                ShowModal(delegate
                {
                    return MessageBox.Show(this, "还没有完成过运行。\n开始一次运行后，这里会显示轮数、动作数、用时和结束原因。",
                        "运行报告", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
                return;
            }
            ShowModal(delegate { return MessageBox.Show(this, lastReport.Describe(), "上次运行报告", MessageBoxButtons.OK, MessageBoxIcon.Information); });
        }

        // Planned rate always; measured rate once a run has produced enough data.
        private void UpdateRateText()
        {
            if (rate == null) return;
            string text = interval.Value < 1000
                ? "设定约 " + (1000m / interval.Value).ToString("0.0") + " 次/秒"
                : "设定每 " + (interval.Value / 1000m).ToString("0.##") + " 秒一次";
            if (engine.Running && engine.Elapsed.TotalSeconds >= 2 && engine.Actions > 0)
                text += " · 实测 " + (engine.Actions / engine.Elapsed.TotalSeconds).ToString("0.0") + " 次/秒";
            rate.Text = text;
        }

        private void LoadSettings()
        {
            if (!File.Exists(configPath)) return;
            try
            {
                Preferences prefs;
                using (FileStream file = File.OpenRead(configPath)) prefs = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(file);
                // Only migrate in memory. The original XML remains available to v1.2.
                prefs.Interval = Math.Max(10, Math.Min(3600000, prefs.Interval));
                prefs.Limit = Math.Max(0, Math.Min(1000000000, prefs.Limit));
                prefs.Button = Math.Max(0, Math.Min(2, prefs.Button));
                prefs.Mode = Math.Max(0, Math.Min(1, prefs.Mode));
                if (prefs.PositionMode < -1 || prefs.PositionMode > 2) prefs.PositionMode = prefs.Fixed ? 1 : 0;
                if (prefs.Positions == null) prefs.Positions = new List<PositionEntry>();
                prefs.Positions.RemoveAll(delegate(PositionEntry point) { return point == null; });
                foreach (PositionEntry point in prefs.Positions) if (point.Button < 0 || point.Button > 2) point.Button = prefs.Button;
                ProfileStore.ValidateSettings(prefs);
                ApplyPreferences(prefs);
            }
            catch (IOException) { hint.Text = "设置读取失败，已使用默认设置。"; }
            catch (UnauthorizedAccessException) { hint.Text = "无法读取设置，已使用默认设置。"; }
            catch (InvalidOperationException) { hint.Text = "设置文件格式无效，已使用默认设置。"; }
        }

        private Preferences CapturePreferences()
        {
            UpdatePointDelays();
            Preferences result = new Preferences { Interval = (int)interval.Value, Button = button.SelectedIndex, Mode = mode.SelectedIndex,
                Limit = (long)limit.Value, Fixed = locationMode.SelectedIndex == 1, Pinned = pin.Checked,
                X = selectedPoint.HasValue ? selectedPoint.Value.X : 0, Y = selectedPoint.HasValue ? selectedPoint.Value.Y : 0,
                HasFixedPoint = selectedPoint.HasValue, PositionMode = locationMode.SelectedIndex,
                Positions = ProfileStore.Copy(positions), ShowMarkers = showMarkers.Checked, DragMarkers = dragMarkers.Checked };
            CopyAdvancedPreferences(result);
            return result;
        }

        private void ApplyPreferences(Preferences prefs)
        {
            loadingPreferences = true;
            try
            {
                ApplyAdvancedPreferences(prefs);
                SetPointEditing(prefs.DragMarkers);
                // Remove the old selection before ValueChanged handlers observe a new interval.
                positionsList.SelectedIndices.Clear();
                positions.Clear();
                positions.AddRange(ProfileStore.Copy(prefs.Positions));
                interval.Value = prefs.Interval;
                limit.Value = prefs.Limit;
                button.SelectedIndex = prefs.Button;
                mode.SelectedIndex = prefs.Mode;
                bool hasPoint = prefs.HasFixedPoint ?? (prefs.Fixed || prefs.PositionMode == 1 || prefs.X != 0 || prefs.Y != 0);
                selectedPoint = hasPoint ? new Point?(new Point(prefs.X, prefs.Y)) : null;
                locationMode.SelectedIndex = prefs.PositionMode < 0 ? (prefs.Fixed ? 1 : 0) : prefs.PositionMode;
                pin.Checked = prefs.Pinned;
                showMarkers.Checked = prefs.ShowMarkers;
                RefreshPositions(positions.Count > 0 ? 0 : -1);
            }
            finally
            {
                loadingPreferences = false;
                dirty = false;
            }
        }

        // A corrupt library is never overwritten silently: the .bak copy is loaded and
        // the broken file is kept beside it under a timestamped name.
        private ProfileLibrary RecoverLibrary(string readPath, string reason)
        {
            string backup = readPath + ".bak";
            if (!File.Exists(backup))
            {
                profileStoreBlocked = true;
                hint.Text = "方案库读取失败，原文件已保留。";
                ShowModal(delegate { return MessageBox.Show(this, "方案库读取失败：" + reason + "\n\n原文件不会被覆盖：\n" + readPath + "\n\n同目录下没有找到 .bak 备份。", "读取方案", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                return null;
            }
            ProfileLibrary recovered;
            try { recovered = ProfileStore.Load(backup); }
            catch (Exception backupError)
            {
                if (!IsFileError(backupError)) throw;
                Log.Error("备份方案库也无法读取：" + backup, backupError);
                profileStoreBlocked = true;
                hint.Text = "方案库和它的备份都无法读取，原文件已保留。";
                ShowModal(delegate { return MessageBox.Show(this, "方案库读取失败，备份也无法使用：\n" + backupError.Message + "\n\n原文件不会被覆盖：\n" + readPath, "读取方案", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                return null;
            }
            string quarantine = readPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(readPath, quarantine, true); }
            catch (Exception copyError)
            {
                if (!IsFileError(copyError)) throw;
                Log.Error("损坏的方案库无法另存", copyError);
                quarantine = readPath;
            }
            Log.Info("已从备份恢复方案库：" + backup);
            hint.Text = "方案库损坏，已自动从备份恢复。";
            ShowModal(delegate { return MessageBox.Show(this, "方案库读取失败，已自动从 .bak 备份恢复。\n\n损坏的原文件已另存为：\n" + quarantine, "读取方案", MessageBoxButtons.OK, MessageBoxIcon.Information); });
            return recovered;
        }

        private ClickProfile CurrentProfile
        {
            get { return library.Profiles.Find(delegate(ClickProfile profile) { return profile.Id == library.SelectedId; }); }
        }

        private void LoadProfiles()
        {
            string readPath = File.Exists(profilesPath) ? profilesPath : Path.ChangeExtension(configPath, ".profiles.json");
            if (File.Exists(readPath))
            {
                try { library = ProfileStore.Load(readPath); }
                catch (Exception ex)
                {
                    if (!IsFileError(ex)) throw;
                    Log.Error("方案库读取失败：" + readPath, ex);
                    library = RecoverLibrary(readPath, ex.Message);
                }
            }
            if (library == null)
            {
                LoadSettings();
                ClickProfile initial = new ClickProfile { Settings = CapturePreferences() };
                library = new ProfileLibrary { SelectedId = initial.Id };
                library.Profiles.Add(initial);
            }
            RefreshProfiles();
            ApplyPreferences(CurrentProfile.Settings);
        }

        private void RefreshProfiles()
        {
            changingProfile = true;
            profileChoice.Items.Clear();
            foreach (ClickProfile profile in library.Profiles)
            {
                profileChoice.Items.Add(profile);
                if (profile.Id == library.SelectedId) profileChoice.SelectedItem = profile;
            }
            changingProfile = false;
        }

        private static bool IsFileError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is NotSupportedException || ex is System.Security.SecurityException;
        }

        private DialogResult ShowModal(Func<DialogResult> show)
        {
            bool previous = modalOpen;
            modalOpen = true;
            SyncMarkers();
            try { return show(); }
            finally { modalOpen = previous; SyncMarkers(); }
        }

        private bool SaveLibrary(ProfileLibrary candidate, bool report)
        {
            try
            {
                if (profileStoreBlocked) throw new IOException("原方案库读取失败，已禁止覆盖。请先导出当前方案，再修复方案库或从 .bak 备份恢复。");
                ProfileStore.Save(profilesPath, candidate);
                library = candidate;
                dirty = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!IsFileError(ex)) throw;
                Log.Error("方案保存失败：" + profilesPath, ex);
                hint.Text = "方案保存失败：" + ex.Message;
                if (report) ShowModal(delegate { return MessageBox.Show(this, "保存失败，原有方案文件已保留。\n" + ex.Message, "保存方案", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                return false;
            }
        }

        private ProfileLibrary EditedLibrary()
        {
            ProfileLibrary candidate = ProfileStore.Copy(library);
            candidate.Profiles.Find(delegate(ClickProfile profile) { return profile.Id == candidate.SelectedId; }).Settings = CapturePreferences();
            return candidate;
        }

        private bool SaveCurrentProfile()
        {
            bool saved = SaveLibrary(EditedLibrary(), true);
            if (saved) RefreshProfiles();
            return saved;
        }

        private void SwitchProfile()
        {
            if (changingProfile || library == null) return;
            ClickProfile next = profileChoice.SelectedItem as ClickProfile;
            if (next == null || next.Id == library.SelectedId) return;
            ProfileLibrary candidate = EditedLibrary();
            candidate.SelectedId = next.Id;
            if (SaveLibrary(candidate, true))
            {
                ApplyPreferences(CurrentProfile.Settings);
                status.Text = "●  已切换方案：" + CurrentProfile.Name;
            }
            RefreshProfiles();
            dirty = false;
        }

        private string UniqueName(string basis)
        {
            string stem = basis.Length > 50 ? basis.Substring(0, 50) : basis;
            string name = stem;
            int suffix = 2;
            while (library.Profiles.Exists(delegate(ClickProfile profile) { return String.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase); }))
                name = stem + " (" + suffix++ + ")";
            return name;
        }

        private string AskProfileName(string title, string initial)
        {
            using (Form dialog = new Form { Text = title, ClientSize = new Size(500, 183), Font = UiFont(10, FontStyle.Regular),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi })
            {
                AddLabel(dialog, "方案名称（最多 60 个字符）", 23, 20, 450, 30, 10, Muted, false);
                TextBox input = new TextBox { Text = initial, MaxLength = 60, Bounds = new Rectangle(23, 60, 455, 38) };
                dialog.Controls.Add(input);
                Button okay = SmallButton(dialog, "确定", 250, 120, 108, delegate { });
                Button cancel = SmallButton(dialog, "取消", 370, 120, 108, delegate { });
                okay.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                dialog.AcceptButton = okay;
                dialog.CancelButton = cancel;
                dialog.Shown += delegate { input.Focus(); input.SelectAll(); };
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return null;
                try { return ProfileStore.ValidateName(input.Text); }
                catch (InvalidDataException ex)
                { ShowModal(delegate { return MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }); return null; }
            }
        }

        private bool CheckName(string name, string exceptId)
        {
            if (!library.Profiles.Exists(delegate(ClickProfile profile) { return profile.Id != exceptId && String.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase); })) return true;
            ShowModal(delegate { return MessageBox.Show(this, "已有同名方案，请使用其他名称。", "方案名称", MessageBoxButtons.OK, MessageBoxIcon.Information); });
            return false;
        }

        private bool CanAddProfile()
        {
            if (library.Profiles.Count < ProfileStore.MaxProfiles) return true;
            status.Text = "●  最多保存 100 套方案，请先导出并删除不用的方案";
            return false;
        }

        private void CreateProfile(bool copy)
        {
            if (!CanAddProfile()) return;
            string name = AskProfileName(copy ? "复制方案" : "新建方案", UniqueName(copy ? CurrentProfile.Name + " 副本" : "新方案"));
            if (name == null || !CheckName(name, null)) return;
            ProfileLibrary candidate = EditedLibrary();
            ClickProfile created = new ClickProfile { Name = name, Settings = copy ? CapturePreferences() : new Preferences() };
            candidate.Profiles.Add(created);
            candidate.SelectedId = created.Id;
            if (!SaveLibrary(candidate, true)) return;
            RefreshProfiles();
            ApplyPreferences(CurrentProfile.Settings);
            status.Text = "●  已" + (copy ? "复制" : "新建") + "方案：" + name;
        }

        private void RenameProfile()
        {
            string name = AskProfileName("重命名方案", CurrentProfile.Name);
            if (name == null || !CheckName(name, library.SelectedId)) return;
            ProfileLibrary candidate = EditedLibrary();
            candidate.Profiles.Find(delegate(ClickProfile profile) { return profile.Id == candidate.SelectedId; }).Name = name;
            if (SaveLibrary(candidate, true)) { RefreshProfiles(); status.Text = "●  方案已重命名"; }
        }

        private void DeleteProfile()
        {
            if (library.Profiles.Count <= 1) { status.Text = "●  至少保留一套方案"; return; }
            if (ShowModal(delegate { return MessageBox.Show(this, "删除方案“" + CurrentProfile.Name + "”及其中的点位？", "删除方案", MessageBoxButtons.YesNo, MessageBoxIcon.Question); }) != DialogResult.Yes) return;
            ProfileLibrary candidate = ProfileStore.Copy(library);
            int index = candidate.Profiles.FindIndex(delegate(ClickProfile profile) { return profile.Id == candidate.SelectedId; });
            candidate.Profiles.RemoveAt(index);
            candidate.SelectedId = candidate.Profiles[Math.Min(index, candidate.Profiles.Count - 1)].Id;
            if (!SaveLibrary(candidate, true)) return;
            RefreshProfiles();
            ApplyPreferences(CurrentProfile.Settings);
            status.Text = "●  已删除方案，当前：" + CurrentProfile.Name;
        }

        private void ImportProfile()
        {
            if (!CanAddProfile()) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "轻点方案 (*.json)|*.json", Title = "导入方案", CheckFileExists = true })
            {
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                try
                {
                    ClickProfile imported = ProfileStore.Import(dialog.FileName);
                    imported.Name = UniqueName(imported.Name);
                    ProfileLibrary candidate = EditedLibrary();
                    candidate.Profiles.Add(imported);
                    candidate.SelectedId = imported.Id;
                    if (!SaveLibrary(candidate, true)) return;
                    RefreshProfiles();
                    ApplyPreferences(CurrentProfile.Settings);
                    status.Text = "●  已导入方案：" + CurrentProfile.Name;
                }
                catch (Exception ex)
                {
                    if (!IsFileError(ex)) throw;
                    ShowModal(delegate { return MessageBox.Show(this, "导入失败，现有方案未更改。\n" + ex.Message, "导入方案", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                }
            }
        }

        private void ExportProfile()
        {
            string filename = CurrentProfile.Name;
            foreach (char invalid in Path.GetInvalidFileNameChars()) filename = filename.Replace(invalid, '_');
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "轻点方案 (*.json)|*.json", Title = "导出当前方案", FileName = filename + ".json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true })
            {
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                try
                {
                    string fullPath = Path.GetFullPath(dialog.FileName);
                    if (String.Equals(fullPath, Path.GetFullPath(profilesPath), StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fullPath, Path.GetFullPath(profilesPath + ".bak"), StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fullPath, Path.GetFullPath(Path.ChangeExtension(configPath, ".profiles.json")), StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fullPath, Path.GetFullPath(Path.ChangeExtension(configPath, ".profiles.json") + ".bak"), StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fullPath, Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("请选择其他文件名，不能用单个方案覆盖内部配置文件。");
                    ProfileStore.Export(dialog.FileName, CurrentProfile.Name, CapturePreferences());
                    status.Text = "●  当前方案已导出";
                }
                catch (Exception ex)
                {
                    if (!IsFileError(ex)) throw;
                    ShowModal(delegate { return MessageBox.Show(this, "导出失败：\n" + ex.Message, "导出方案", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exitRequested && advanced.TrayOnMinimize && tray != null)
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                e.Cancel = true;
                if (library == null || SaveLibrary(EditedLibrary(), true)) HideToTray();
                return;
            }
            timer.Stop();
            StopRun("已停止");
            if (library != null && !SaveLibrary(EditedLibrary(), false))
            {
                if (ShowModal(delegate { return MessageBox.Show(this, "当前方案未能保存。\n" + hint.Text + "\n\n仍要退出？选择“否”可返回并导出方案。", "保存失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning); }) != DialogResult.Yes)
                { e.Cancel = true; exitRequested = false; timer.Start(); RestoreWindow(); base.OnFormClosing(e); return; }
            }
            markersReady = false;
            markerLayer.Dispose();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAdvanced();
                markersReady = false; markerLayer.Dispose(); timer.Dispose(); engine.Dispose();
                if (badgeImages != null) badgeImages.Dispose();
                badgeCache.Dispose();
                macroBadgeCache.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
