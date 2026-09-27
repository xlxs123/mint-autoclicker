using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MintClicker
{
    internal sealed partial class MainForm
    {
        private Preferences advanced = new Preferences();
        private readonly List<MacroEntry> macroEntries = new List<MacroEntry>();
        private NumericUpDown pointCount, pointRepeatInterval, loopStart, loopEnd, loopCount;
        private TabControl editors;
        private ListView macroList;
        private Panel macroPanel;
        private Button pauseButton, optionsButton;
        private Label runDetail, targetLabel;
        private ComboBox macroKind;
        private bool refreshingMacro, selectingTab, targetCapturing, exitRequested;
        private IntPtr targetWindow;
        private uint targetProcessId;
        private int highlightedNumber = -1, highlightedMode = -1;
        private NotifyIcon tray;
        private ToolStripMenuItem trayStart, trayPause, trayStop;
        private ImageList macroImages;
        private readonly List<Bitmap> macroBitmaps = new List<Bitmap>();

        private void InitializeAdvanced()
        {
            start.Width = 232;
            stopButton.Left = 432;
            stopButton.Width = 144;
            pauseButton = ActionButton("暂停  F9", 266, 586, 156, Color.FromArgb(226, 233, 227), Ink);
            pauseButton.Enabled = false;
            pauseButton.Click += delegate { PauseRun(); };
            optionsButton = SmallButton(this, "更多设置", 610, 32, 124, delegate { ShowOptions(); });
            runDetail = AddLabel(this, "就绪 · 启动后显示当前等待和重复进度", 24, 644, 552, 54, 10, Muted, false);
            targetLabel = AddLabel(this, "目标保护：未启用", 24, 708, 552, 42, 9, Muted, false);
            hint.SetBounds(24, 764, 552, 40);
            hint.Text = "F6 开始/停止 · F7 取点 · F9 暂停/恢复\nF8 / F12 或主屏左上角紧急停止";

            editors = new TabControl { Bounds = new Rectangle(594, 144, 442, 652) };
            TabPage pointsPage = new TabPage("点击点"), macroPage = new TabPage("动作宏");
            pointsPage.BackColor = macroPage.BackColor = Color.White;
            Controls.Remove(positionsPanel);
            positionsPanel.Location = Point.Empty;
            positionsPanel.Height = 616;
            pointsPage.Controls.Add(positionsPanel);
            editors.TabPages.Add(pointsPage);
            editors.TabPages.Add(macroPage);
            Controls.Add(editors);
            AddLabel(positionsPanel, "每点动作次数", 18, 545, 180, 23, 9, Muted, false);
            AddLabel(positionsPanel, "点内间隔（毫秒）", 216, 545, 188, 23, 9, Muted, false);
            pointCount = Number(positionsPanel, "每点动作次数", 18, 574, 180, 1, 10000, 1);
            pointRepeatInterval = Number(positionsPanel, "点内重复间隔", 216, 574, 188, 0, 3600000, 100);
            pointCount.ValueChanged += delegate { UpdatePointDelays(); };
            pointRepeatInterval.ValueChanged += delegate { UpdatePointDelays(); };

            macroPanel = new Panel { Bounds = new Rectangle(0, 0, 426, 616), BackColor = Color.White };
            macroPage.Controls.Add(macroPanel);
            AddLabel(macroPanel, "动作顺序", 18, 15, 380, 30, 13, Ink, true);
            AddLabel(macroPanel, "只执行勾选项 · 双击编辑 · F7 添加点击步骤", 18, 49, 390, 24, 9, Muted, false);
            macroList = new ListView { Bounds = new Rectangle(18, 80, 386, 283), View = View.Details,
                CheckBoxes = true, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true, Font = new Font("Microsoft YaHei UI", 9) };
            macroList.Columns.Add("步骤", 356);
            macroPanel.Controls.Add(macroList);
            macroList.ItemChecked += delegate(object sender, ItemCheckedEventArgs args)
            {
                if (!refreshingMacro && args.Item.Index >= 0 && args.Item.Index < macroEntries.Count)
                { macroEntries[args.Item.Index].Enabled = args.Item.Checked; UpdatePosition(); }
            };
            macroList.DoubleClick += delegate { EditMacro(); };
            macroKind = Choice(macroPanel, "新增动作类型", 18, 377, new[] { "点击", "等待", "按键", "移动鼠标", "滚轮" });
            macroKind.Width = 240;
            SmallButton(macroPanel, "添加步骤", 270, 375, 134, delegate { AddMacro(); });
            SmallButton(macroPanel, "编辑", 18, 421, 88, delegate { EditMacro(); });
            SmallButton(macroPanel, "删除", 116, 421, 88, delegate
            {
                int index = SelectedMacro;
                if (index < 0) return;
                macroEntries.RemoveAt(index); RefreshMacro(Math.Min(index, macroEntries.Count - 1));
            });
            SmallButton(macroPanel, "上移", 216, 421, 88, delegate { MoveMacro(-1); });
            SmallButton(macroPanel, "下移", 314, 421, 90, delegate { MoveMacro(1); });
            SmallButton(macroPanel, "从点击点追加转换", 18, 469, 236, delegate { ConvertPoints(); });
            SmallButton(macroPanel, "使用此宏", 266, 469, 138, delegate { locationMode.SelectedIndex = 3; });
            AddLabel(macroPanel, "局部循环：按列表编号；起点 0 表示关闭", 18, 516, 390, 22, 9, Muted, false);
            AddLabel(macroPanel, "起点", 18, 542, 100, 22, 9, Muted, false);
            AddLabel(macroPanel, "终点", 152, 542, 100, 22, 9, Muted, false);
            AddLabel(macroPanel, "次数", 286, 542, 100, 22, 9, Muted, false);
            loopStart = Number(macroPanel, "局部循环起点", 18, 570, 116, 0, 1000, 0);
            loopEnd = Number(macroPanel, "局部循环终点", 152, 570, 116, 0, 1000, 0);
            loopCount = Number(macroPanel, "局部循环次数", 286, 570, 118, 1, 10000, 1);
            editors.SelectedIndexChanged += delegate
            {
                if (selectingTab || library == null) return;
                locationMode.SelectedIndex = editors.SelectedIndex == 1 ? 3 : 2;
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            trayStart = new ToolStripMenuItem("开始（F6）", null, delegate { if (!engine.Running && !pending && !capturing && !targetCapturing && !modalOpen) Toggle(); });
            trayPause = new ToolStripMenuItem("暂停/恢复（F9）", null, delegate { PauseRun(); });
            trayStop = new ToolStripMenuItem("停止（F8）", null, delegate { StopRun("已通过托盘停止"); });
            menu.Items.AddRange(new ToolStripItem[] { trayStart, trayPause, trayStop, new ToolStripSeparator(),
                new ToolStripMenuItem("显示主窗口", null, delegate { RestoreWindow(); }), new ToolStripMenuItem("退出", null, delegate { RequestExit(); }) });
            tray = new NotifyIcon { Icon = Icon, Text = "轻点 v2.0.1 · 已就绪", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { RestoreWindow(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && advanced.TrayOnMinimize && tray != null)
                { Hide(); SyncMarkers(); }
            };
        }

        private int SelectedMacro { get { return macroList.SelectedIndices.Count == 0 ? -1 : macroList.SelectedIndices[0]; } }
        private void SelectEditorTab()
        {
            if (editors == null) return;
            selectingTab = true;
            editors.SelectedIndex = locationMode.SelectedIndex == 3 ? 1 : 0;
            selectingTab = false;
        }
        private void RefreshMacro(int selected)
        {
            refreshingMacro = true;
            macroList.BeginUpdate();
            macroList.Items.Clear();
            macroList.SmallImageList = null;
            if (macroImages != null) macroImages.Dispose();
            foreach (Bitmap bitmap in macroBitmaps) bitmap.Dispose();
            macroBitmaps.Clear();
            macroImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(MarkerSize, MarkerSize) };
            for (int i = 0; i < macroEntries.Count; i++)
            {
                MacroEntry item = macroEntries[i];
                Bitmap bitmap = PointBadge.Image(i + 1, item.Kind == ActionKind.Click ? item.Button : 3, MarkerSize);
                macroBitmaps.Add(bitmap); macroImages.Images.Add(bitmap);
                macroList.Items.Add(new ListViewItem(ActionCompiler.Name(item.Kind) + " · " + ActionCompiler.Description(item), i)
                { Checked = item.Enabled, ToolTipText = "第 " + (i + 1) + " 项：" + ActionCompiler.Description(item) + "\n前等待 " + item.DelayBefore + " ms；后等待 " + item.DelayAfter + " ms" });
            }
            macroList.SmallImageList = macroImages;
            if (selected >= 0 && selected < macroEntries.Count) { macroList.Items[selected].Selected = true; macroList.EnsureVisible(selected); }
            macroList.EndUpdate();
            refreshingMacro = false;
            UpdatePosition();
        }
        private void AddMacro()
        {
            if (macroEntries.Count >= ProfileStore.MaxPoints) { status.Text = "●  最多 1000 个宏步骤"; return; }
            Point point = selectedPoint ?? Native.CursorPoint();
            MacroEntry entry = new MacroEntry { Kind = (ActionKind)macroKind.SelectedIndex, X = point.X, Y = point.Y, Button = button.SelectedIndex };
            using (ActionEditor dialog = new ActionEditor(entry))
            {
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                macroEntries.Add(dialog.Result); RefreshMacro(macroEntries.Count - 1);
            }
        }
        private void EditMacro()
        {
            int index = SelectedMacro;
            if (index < 0 || engine.Running || pending) return;
            using (ActionEditor dialog = new ActionEditor(macroEntries[index]))
            {
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                macroEntries[index] = dialog.Result; RefreshMacro(index);
            }
        }
        private void MoveMacro(int delta)
        {
            int index = SelectedMacro, next = index + delta;
            if (index < 0 || next < 0 || next >= macroEntries.Count) return;
            MacroEntry value = macroEntries[index]; macroEntries.RemoveAt(index); macroEntries.Insert(next, value); RefreshMacro(next);
        }
        private void ConvertPoints()
        {
            UpdatePointDelays();
            if (macroEntries.Count + positions.Count > ProfileStore.MaxPoints) { status.Text = "●  转换后将超过 1000 步"; return; }
            foreach (PositionEntry point in positions) macroEntries.Add(new MacroEntry { Kind = ActionKind.Click, X = point.X, Y = point.Y,
                Enabled = point.Enabled, Button = point.Button, DelayBefore = point.DelayBefore, DelayAfter = point.DelayAfter < 0 ? (int)interval.Value : point.DelayAfter,
                Count = point.Count, RepeatInterval = point.RepeatInterval, DoubleClick = mode.SelectedIndex == 1 });
            RefreshMacro(macroEntries.Count - 1);
            status.Text = "●  已追加为独立宏步骤，原点击点保留";
        }

        private void CopyAdvancedPreferences(Preferences prefs)
        {
            prefs.Actions = ProfileStore.Copy(macroEntries);
            prefs.StartupDelay = advanced.StartupDelay;
            prefs.TrayOnMinimize = advanced.TrayOnMinimize;
            prefs.StartMinimized = advanced.StartMinimized;
            prefs.ProtectTarget = advanced.ProtectTarget;
            prefs.TargetTitle = advanced.TargetTitle;
            prefs.TargetProcess = advanced.TargetProcess;
            prefs.LoopStart = (int)loopStart.Value; prefs.LoopEnd = (int)loopEnd.Value; prefs.LoopCount = (int)loopCount.Value;
        }
        private void ApplyAdvancedPreferences(Preferences prefs)
        {
            advanced = ProfileStore.Copy(prefs);
            targetWindow = IntPtr.Zero; targetProcessId = 0;
            macroEntries.Clear(); macroEntries.AddRange(ProfileStore.Copy(prefs.Actions));
            loopStart.Value = prefs.LoopStart; loopEnd.Value = prefs.LoopEnd; loopCount.Value = prefs.LoopCount;
            RefreshMacro(macroEntries.Count > 0 ? 0 : -1);
            UpdateTargetLabel();
        }
        private void SetAdvancedBusy(bool busy)
        {
            if (editors != null) editors.Enabled = !busy;
            if (optionsButton != null) optionsButton.Enabled = !busy;
            if (pauseButton != null) pauseButton.Enabled = engine.Running;
        }
        private void PauseRun()
        {
            if (modalOpen) return;
            engine.TogglePause(); UpdateRunDisplay();
            if (engine.Running) status.Text = "●  " + engine.Reason;
        }
        private void StartPendingRun()
        {
            pending = false; countdown.Stop();
            try
            {
                positionsList.SelectedIndices.Clear(); macroList.SelectedIndices.Clear();
                highlightedNumber = -1;
                engine.Start(pendingOptions); wasRunning = true; SetBusy(true);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidDataException)) throw;
                status.Text = "●  " + ex.Message; SetBusy(false);
            }
        }
        private bool PrepareAdvancedRun(ClickOptions options)
        {
            if (locationMode.SelectedIndex == 3)
            {
                List<MacroEntry> selected = new List<MacroEntry>();
                List<int> numbers = new List<int>();
                for (int i = 0; i < macroEntries.Count; i++)
                {
                    MacroEntry entry = macroEntries[i];
                    if (!entry.Enabled) continue;
                    if (entry.Kind == ActionKind.Click || entry.Kind == ActionKind.Move || entry.Kind == ActionKind.Scroll)
                    {
                        string error = PositionError(new Point(entry.X, entry.Y));
                        if (error != null) { status.Text = "●  步骤 " + (i + 1) + "：" + error; return false; }
                    }
                    selected.Add(entry); numbers.Add(i + 1);
                }
                if (selected.Count == 0) { status.Text = "●  请添加并勾选至少一个宏步骤"; return false; }
                options.Actions = ProfileStore.Copy(selected).ToArray(); options.SourceNumbers = numbers.ToArray();
                if (loopStart.Value > 0 && loopCount.Value > 1)
                {
                    options.LoopStart = numbers.IndexOf((int)loopStart.Value);
                    options.LoopEnd = numbers.IndexOf((int)loopEnd.Value);
                    options.LoopCount = (int)loopCount.Value;
                    if (options.LoopStart < 0 || options.LoopEnd < options.LoopStart)
                    { status.Text = "●  局部循环的起点、终点必须已勾选且顺序正确"; return false; }
                }
            }
            if (advanced.ProtectTarget)
            {
                uint process;
                Native.GetWindowThreadProcessId(targetWindow, out process);
                if (!Native.IsWindow(targetWindow) || process != targetProcessId)
                {
                    targetWindow = Native.FindTarget(advanced.TargetTitle, advanced.TargetProcess);
                    Native.GetWindowThreadProcessId(targetWindow, out targetProcessId);
                }
                if (targetWindow == IntPtr.Zero || targetWindow == Handle)
                { status.Text = "●  未找到唯一目标窗口，请在“更多设置”重新绑定"; return false; }
                options.TargetWindow = targetWindow; options.TargetProcessId = targetProcessId;
            }
            return true;
        }
        private void UpdateRunDisplay()
        {
            if (pauseButton == null) return;
            pauseButton.Enabled = engine.Running;
            pauseButton.Text = engine.Paused ? "恢复  F9" : "暂停  F9";
            if (engine.Running)
            {
                string limitText = pendingOptions.Limit == 0 ? "不限" : pendingOptions.Limit.ToString();
                runDetail.Text = (engine.Paused ? "已暂停 · " : "") + engine.Phase + " · 剩余 " + engine.Remaining + " ms\n已完成轮数 " + engine.Rounds + " / " + limitText +
                    (engine.Repetitions > 0 ? " · 点内动作 " + engine.Repetition + " / " + engine.Repetitions : "");
            }
            else if (!pending && !capturing && !targetCapturing) runDetail.Text = "已完成 " + engine.Rounds + " 轮 · " + engine.Reason;
            int current = engine.Running ? engine.SourceNumber : 0;
            int modeIndex = locationMode.SelectedIndex;
            if (current != highlightedNumber || modeIndex != highlightedMode)
            {
                foreach (ListViewItem item in positionsList.Items) item.BackColor = Color.White;
                foreach (ListViewItem item in macroList.Items) item.BackColor = Color.White;
                ListView list = modeIndex == 3 ? macroList : modeIndex == 2 ? positionsList : null;
                if (list != null && current > 0 && current <= list.Items.Count)
                { list.Items[current - 1].BackColor = Color.FromArgb(255, 237, 171); list.Items[current - 1].EnsureVisible(); }
                markerLayer.Highlight(current);
                highlightedNumber = current; highlightedMode = modeIndex;
            }
            if (tray != null)
            {
                trayStart.Enabled = !engine.Running && !pending && !capturing && !targetCapturing && !modalOpen;
                trayPause.Enabled = engine.Running && !modalOpen;
                trayPause.Text = engine.Paused ? "恢复（F9）" : "暂停（F9）";
                trayStop.Enabled = engine.Running || pending || capturing || targetCapturing;
                tray.Text = "轻点 v2.0.1 · " + (engine.Running ? engine.Paused ? "已暂停" : "运行中 · 第 " + current + " 项" : "已停止");
            }
        }
        private void RestoreWindow() { WindowState = FormWindowState.Normal; Show(); Activate(); }
        private void HideToTray()
        {
            tray.Visible = true;
            WindowState = FormWindowState.Minimized;
            Hide();
            SyncMarkers();
        }
        private void RequestExit()
        {
            if (modalOpen) { RestoreWindow(); return; }
            exitRequested = true;
            Close();
            if (!IsDisposed) exitRequested = false;
        }

        private void UpdateTargetLabel()
        {
            if (targetLabel != null) targetLabel.Text = advanced.ProtectTarget ? "目标保护：" + (advanced.TargetTitle.Length == 0 ? "尚未绑定" : advanced.TargetTitle) : "目标保护：未启用";
        }
        private void BeginTargetCapture()
        {
            if (engine.Running || pending || capturing) return;
            targetCapturing = true; countdown.Restart(); SetBusy(true);
            status.Text = "●  3 秒后绑定，请切到目标窗口";
        }
        private bool TickTargetCapture()
        {
            if (!targetCapturing) return false;
            if (countdown.ElapsedMilliseconds < 3000)
            { status.Text = "●  " + (int)Math.Ceiling((3000 - countdown.ElapsedMilliseconds) / 1000.0) + " 秒后绑定，请切到目标窗口"; return true; }
            targetCapturing = false; countdown.Stop(); SetBusy(false);
            IntPtr selected = Native.GetForegroundWindow();
            string title = Native.WindowTitle(selected), process = Native.ProcessName(selected);
            if (selected == IntPtr.Zero || selected == Handle || title.Length == 0 || process.Length == 0)
            { status.Text = "●  没有选到有效的外部窗口，请重试"; return true; }
            targetWindow = selected; Native.GetWindowThreadProcessId(selected, out targetProcessId);
            advanced.TargetTitle = title; advanced.TargetProcess = process; advanced.ProtectTarget = true;
            UpdateTargetLabel(); status.Text = "●  已绑定目标窗口并启用保护";
            return true;
        }

        private static CheckBox OptionCheck(Control parent, string text, int y, bool value)
        {
            CheckBox check = new CheckBox { Text = text, Checked = value, Bounds = new Rectangle(20, y, 440, 30) };
            parent.Controls.Add(check); return check;
        }
        private void ShowOptions()
        {
            using (Form dialog = new Form { Text = "更多设置", ClientSize = new Size(484, 440), Font = Font,
                AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false })
            {
                AddLabel(dialog, "启动倒计时", 20, 26, 170, 26, 10, Muted, false);
                ComboBox delay = Choice(dialog, "启动倒计时", 200, 20, new[] { "立即开始", "1 秒", "3 秒", "5 秒", "10 秒" });
                int[] values = { 0, 1, 3, 5, 10 }; delay.SelectedIndex = Array.IndexOf(values, advanced.StartupDelay);
                CheckBox protect = OptionCheck(dialog, "仅在绑定的目标窗口处于前台时执行", 77, advanced.ProtectTarget);
                AddLabel(dialog, advanced.TargetTitle.Length == 0 ? "尚未绑定目标窗口" : advanced.TargetTitle, 20, 115, 442, 36, 9, Muted, false);
                Button bind = SmallButton(dialog, "3 秒后绑定目标窗口", 20, 158, 280, delegate { });
                bind.DialogResult = DialogResult.Retry;
                CheckBox minimize = OptionCheck(dialog, "关闭或最小化时收进系统托盘", 210, advanced.TrayOnMinimize);
                CheckBox startMin = OptionCheck(dialog, "下次启动时最小化（不会自动执行）", 247, advanced.StartMinimized);
                bool originalStartup = ReadLoginStartup();
                CheckBox login = OptionCheck(dialog, "登录 Windows 时启动（当前用户）", 284, originalStartup);
                AddLabel(dialog, "F6 开始/停止 · F7 / Ctrl+Alt+A 取点\nF9 暂停/恢复 · F8 / F12 紧急停止", 20, 323, 444, 46, 9, Muted, false);
                Button okay = SmallButton(dialog, "应用", 264, 389, 96, delegate { }); okay.DialogResult = DialogResult.OK;
                Button cancel = SmallButton(dialog, "取消", 368, 389, 96, delegate { }); cancel.DialogResult = DialogResult.Cancel;
                dialog.AcceptButton = okay; dialog.CancelButton = cancel;
                DialogResult result = ShowModal(delegate { return dialog.ShowDialog(this); });
                if (result != DialogResult.OK && result != DialogResult.Retry) return;
                advanced.StartupDelay = values[delay.SelectedIndex]; advanced.ProtectTarget = protect.Checked;
                advanced.TrayOnMinimize = minimize.Checked; advanced.StartMinimized = startMin.Checked;
                if (login.Checked != originalStartup)
                {
                    try { SetLoginStartup(login.Checked); }
                    catch (Exception ex)
                    { if (!IsFileError(ex)) throw; ShowModal(delegate { return MessageBox.Show(this, "登录启动设置失败：" + ex.Message, "更多设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }); }
                }
                UpdateTargetLabel();
                if (result == DialogResult.Retry) BeginTargetCapture();
                else status.Text = "●  设置已应用；方案切换或退出时保存";
            }
        }
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static bool ReadLoginStartup()
        {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("MintClicker") != null; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static void SetLoginStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new IOException("无法访问当前用户的登录启动设置。");
                if (enabled) key.SetValue("MintClicker", "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                else key.DeleteValue("MintClicker", false);
            }
        }
        private void DisposeAdvanced()
        {
            if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); tray = null; }
            if (macroImages != null) { macroImages.Dispose(); macroImages = null; }
            foreach (Bitmap bitmap in macroBitmaps) bitmap.Dispose(); macroBitmaps.Clear();
        }
    }
}
