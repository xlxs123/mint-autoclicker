using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
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
        private Button pauseButton, optionsButton, recordButton;
        private Label runDetail, targetLabel;
        private ComboBox macroKind;
        private bool refreshingMacro, selectingTab, targetCapturing, exitRequested;
        private IntPtr targetWindow;
        private uint targetProcessId;
        private int highlightedNumber = -1, highlightedMode = -1;
        private bool pauseNotified;
        private string hotkeyWarning = "";
        private string lastHotkeyFailure = "";
        private NotifyIcon tray;
        private ToolStripMenuItem trayStart, trayPause, trayStop;
        private ImageList macroImages;
        private readonly List<MacroEntry> macroClipboard = new List<MacroEntry>();

        private void InitializeAdvanced()
        {
            start.Width = 290;
            stopButton.Left = 540;
            stopButton.Width = 180;
            pauseButton = ActionButton("暂停  F9", 333, 733, 195, Color.FromArgb(226, 233, 227), Ink);
            pauseButton.Enabled = false;
            pauseButton.Click += delegate { PauseRun(); };
            optionsButton = SmallButton(this, "更多设置", 763, 40, 155, delegate { ShowOptions(); });
            // Left column under the action buttons, inside the 860 px client height:
            // multi-line run status (3 lines at runtime), then the target-protection line.
            runDetail = AddLabel(this, "就绪 · 启动后显示当前等待和重复进度", 30, 795, 690, 52, 10, Muted, false);
            targetLabel = AddLabel(this, "目标保护：未启用", 30, 848, 690, 26, 9, Muted, false);
            targetLabel.AutoEllipsis = true;
            // The old 2-line hotkey footnote is gone: at this font size it no longer fits
            // under the action buttons without pushing the window past the work area. Those
            // keys are already printed on the buttons themselves ("开始连点 F6", "暂停 F9",
            // "停止 F8") and listed in README.md.
            hint.Visible = false;

            // 640 + 37 px of tab chrome = 677, so the tab control bottom lands at 857 --
            // inside the 860 client height. A taller control would make the form scroll.
            editors = new TabControl { Bounds = new Rectangle(743, 180, 552, 677) };
            TabPage pointsPage = new TabPage("点击点"), macroPage = new TabPage("动作宏");
            pointsPage.BackColor = macroPage.BackColor = Color.White;
            Controls.Remove(positionsPanel);
            positionsPanel.Location = Point.Empty;
            positionsPanel.Height = 636;
            pointsPage.Controls.Add(positionsPanel);
            editors.TabPages.Add(pointsPage);
            editors.TabPages.Add(macroPage);
            Controls.Add(editors);

            // The macro tab is laid out as two columns: the step list with its type picker
            // on the left, the edit actions and loop settings on the right.
            macroPanel = new Panel { Bounds = new Rectangle(0, 0, 533, 636), BackColor = Color.White };
            macroPage.Controls.Add(macroPanel);
            AddLabel(macroPanel, "动作顺序", 23, 19, 475, 38, 13, Ink, true);
            AddLabel(macroPanel, "只执行勾选项 · 双击编辑 · F7 添加点击步骤", 23, 61, 488, 30, 9, Muted, false);
            macroList = new ListView { Bounds = new Rectangle(23, 100, 230, 395), View = View.Details,
                CheckBoxes = true, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true, Font = UiFont(9, FontStyle.Regular) };
            macroList.Columns.Add("步骤", 200);
            macroPanel.Controls.Add(macroList);
            macroList.ItemChecked += delegate(object sender, ItemCheckedEventArgs args)
            {
                // Same replay behaviour as the point list: reconcile after the burst.
                if (refreshingMacro || !IsHandleCreated) return;
                BeginInvoke(new Action(SyncMacroChecked));
            };
            macroList.DoubleClick += delegate { EditMacro(); };
            ContextMenuStrip macroMenu = new ContextMenuStrip();
            ToolStripMenuItem copyItem = new ToolStripMenuItem("复制（Ctrl+C）", null, delegate { CopyMacro(); });
            ToolStripMenuItem pasteItem = new ToolStripMenuItem("粘贴（Ctrl+V）", null, delegate { PasteMacro(); });
            macroMenu.Items.AddRange(new ToolStripItem[] { copyItem, pasteItem, new ToolStripSeparator(),
                new ToolStripMenuItem("上移", null, delegate { MoveMacro(-1); }),
                new ToolStripMenuItem("下移", null, delegate { MoveMacro(1); }),
                new ToolStripMenuItem("删除", null, delegate { DeleteSelectedMacro(); }) });
            macroMenu.Opening += delegate
            {
                copyItem.Enabled = macroList.SelectedIndices.Count > 0;
                pasteItem.Enabled = macroClipboard.Count > 0;
            };
            macroList.ContextMenuStrip = macroMenu;
            macroList.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (!args.Control) return;
                if (args.KeyCode == Keys.C) { CopyMacro(); args.Handled = true; }
                else if (args.KeyCode == Keys.V) { PasteMacro(); args.Handled = true; }
            };
            macroKind = Choice(macroPanel, "新增动作类型", 23, 513, new[] { "点击", "等待", "按键", "移动鼠标", "滚轮", "等颜色", "拖动", "找图" });
            macroKind.Width = 230;
            SmallButton(macroPanel, "编辑", 270, 100, 116, delegate { EditMacro(); });
            SmallButton(macroPanel, "删除", 393, 100, 116, delegate
            {
                int index = SelectedMacro;
                if (index < 0) return;
                macroEntries.RemoveAt(index); RefreshMacro(Math.Min(index, macroEntries.Count - 1));
            });
            SmallButton(macroPanel, "上移", 270, 155, 116, delegate { MoveMacro(-1); });
            SmallButton(macroPanel, "下移", 393, 155, 116, delegate { MoveMacro(1); });
            SmallButton(macroPanel, "添加步骤", 270, 210, 239, delegate { AddMacro(); });
            SmallButton(macroPanel, "使用此宏", 270, 265, 239, delegate { locationMode.SelectedIndex = 3; });
            recordButton = SmallButton(macroPanel, "录制操作…", 270, 474, 239, delegate { ToggleRecording(); });
            AddLabel(macroPanel, "局部循环", 270, 325, 256, 28, 9, Muted, false);
            AddLabel(macroPanel, "起点", 270, 354, 77, 28, 9, Muted, false);
            AddLabel(macroPanel, "终点", 358, 354, 77, 28, 9, Muted, false);
            AddLabel(macroPanel, "次数", 446, 354, 77, 28, 9, Muted, false);
            loopStart = Number(macroPanel, "局部循环起点", 270, 382, 80, 0, 1000, 0);
            loopEnd = Number(macroPanel, "局部循环终点", 358, 382, 80, 0, 1000, 0);
            loopCount = Number(macroPanel, "局部循环次数", 446, 382, 80, 1, 10000, 1);
            AddLabel(macroPanel, "按列表编号，起点 0 为关闭", 270, 438, 256, 28, 9, Muted, false);
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
                new ToolStripMenuItem("运行报告…", null, delegate { ShowRunReport(); }),
                new ToolStripMenuItem("显示主窗口", null, delegate { RestoreWindow(); }), new ToolStripMenuItem("退出", null, delegate { RequestExit(); }) });
            tray = new NotifyIcon { Icon = Icon, Text = AppInfo.TrayTitle + " · 已就绪", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { RestoreWindow(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && advanced.TrayOnMinimize && tray != null)
                { Hide(); SyncMarkers(); }
            };
            UpdateHotkeyLabels();
        }

        private int SelectedMacro { get { return macroList.SelectedIndices.Count == 0 ? -1 : macroList.SelectedIndices[0]; } }
        private void SelectEditorTab()
        {
            if (editors == null) return;
            selectingTab = true;
            editors.SelectedIndex = locationMode.SelectedIndex == 3 ? 1 : 0;
            selectingTab = false;
        }
        private void SyncMacroChecked()
        {
            if (refreshingMacro || IsDisposed) return;
            bool changed = false;
            int count = Math.Min(macroEntries.Count, macroList.Items.Count);
            for (int i = 0; i < count; i++)
            {
                if (macroEntries[i].Enabled == macroList.Items[i].Checked) continue;
                macroEntries[i].Enabled = macroList.Items[i].Checked;
                changed = true;
            }
            if (!changed) return;
            MarkDirty();
            UpdatePosition();
        }

        private void RefreshMacro(int selected)
        {
            MarkDirty();
            refreshingMacro = true;
            macroList.BeginUpdate();
            macroList.Items.Clear();
            macroList.SmallImageList = null;
            if (macroImages != null) macroImages.Dispose();
            macroImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(MarkerSize, MarkerSize) };
            macroBadgeCache.BeginRefresh();
            for (int i = 0; i < macroEntries.Count; i++)
            {
                MacroEntry item = macroEntries[i];
                macroImages.Images.Add(macroBadgeCache.Get(i + 1, item.Kind == ActionKind.Click ? item.Button : 3, MarkerSize));
                macroList.Items.Add(new ListViewItem(ActionCompiler.Name(item.Kind) + " · " + ActionCompiler.Description(item), i)
                { Checked = item.Enabled, ToolTipText = "第 " + (i + 1) + " 项：" + ActionCompiler.Description(item) + "\n前等待 " + item.DelayBefore + " ms；后等待 " + item.DelayAfter + " ms" });
            }
            macroBadgeCache.Trim();
            macroList.SmallImageList = macroImages;
            if (selected >= 0 && selected < macroEntries.Count) { macroList.Items[selected].Selected = true; macroList.EnsureVisible(selected); }
            macroList.EndUpdate();
            refreshingMacro = false;
            UpdatePosition();
        }
        private List<int> SelectedMacroIndices
        {
            get
            {
                List<int> indices = new List<int>();
                foreach (int index in macroList.SelectedIndices) indices.Add(index);
                indices.Sort();
                return indices;
            }
        }

        private void CopyMacro()
        {
            List<int> indices = SelectedMacroIndices;
            if (indices.Count == 0) return;
            macroClipboard.Clear();
            foreach (int index in indices) macroClipboard.Add(ProfileStore.Copy(macroEntries[index]));
            status.Text = "●  已复制 " + macroClipboard.Count + " 个步骤";
        }

        private void PasteMacro()
        {
            if (macroClipboard.Count == 0) return;
            if (engine.Running || pending) { status.Text = "●  请先停止运行再粘贴"; return; }
            int room = ProfileStore.MaxPoints - macroEntries.Count;
            if (room <= 0) { status.Text = "●  宏步骤已满"; return; }
            List<MacroEntry> copies = ProfileStore.Copy(macroClipboard);
            if (copies.Count > room) copies.RemoveRange(room, copies.Count - room);
            int insert = SelectedMacro;
            insert = insert < 0 ? macroEntries.Count : insert + 1;
            macroEntries.InsertRange(insert, copies);
            RefreshMacro(insert + copies.Count - 1);
            status.Text = "●  已粘贴 " + copies.Count + " 个步骤";
        }

        private void DeleteSelectedMacro()
        {
            List<int> indices = SelectedMacroIndices;
            if (indices.Count == 0) return;
            for (int i = indices.Count - 1; i >= 0; i--) macroEntries.RemoveAt(indices[i]);
            RefreshMacro(Math.Min(indices[0], macroEntries.Count - 1));
        }

        private void AddMacro()
        {
            if (macroEntries.Count >= ProfileStore.MaxPoints) { status.Text = "●  最多 1000 个宏步骤"; return; }
            Point point = selectedPoint ?? ScreenToStored(Native.CursorPoint());
            MacroEntry entry = new MacroEntry { Kind = (ActionKind)macroKind.SelectedIndex, X = point.X, Y = point.Y, Button = button.SelectedIndex };
            using (ActionEditor dialog = new ActionEditor(entry, delegate(Point screen) { return ScreenToStored(screen); }))
            {
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                macroEntries.Add(dialog.Result); RefreshMacro(macroEntries.Count - 1);
            }
        }
        private void EditMacro()
        {
            int index = SelectedMacro;
            if (index < 0 || engine.Running || pending) return;
            using (ActionEditor dialog = new ActionEditor(macroEntries[index], delegate(Point screen) { return ScreenToStored(screen); }))
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
            prefs.HighPrecision = advanced.HighPrecision;
            prefs.RelativeCoordinates = advanced.RelativeCoordinates;
            prefs.TargetTitle = advanced.TargetTitle;
            prefs.TargetProcess = advanced.TargetProcess;
            prefs.JitterInterval = advanced.JitterInterval;
            prefs.JitterPosition = advanced.JitterPosition;
            prefs.RunMinutes = advanced.RunMinutes;
            prefs.ScheduleEnabled = advanced.ScheduleEnabled;
            prefs.ScheduleHour = advanced.ScheduleHour;
            prefs.ScheduleMinute = advanced.ScheduleMinute;
            prefs.HotkeyStart = advanced.HotkeyStart;
            prefs.HotkeyCapture = advanced.HotkeyCapture;
            prefs.HotkeyStop = advanced.HotkeyStop;
            prefs.HotkeyPause = advanced.HotkeyPause;
            prefs.HotkeyCapture2 = advanced.HotkeyCapture2;
            prefs.HotkeyEmergency = advanced.HotkeyEmergency;
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
            ApplyHotkeys();
        }
        private void SetAdvancedBusy(bool busy)
        {
            if (editors != null) editors.Enabled = !busy;
            if (optionsButton != null) optionsButton.Enabled = !busy;
            if (pauseButton != null) pauseButton.Enabled = engine.Running;
            if (recordButton != null) recordButton.Enabled = !busy || recorder.Recording;
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
                engine.Start(pendingOptions); wasRunning = true; SetBusy(true); runStarted = DateTime.Now;
                Log.Info("开始运行 · " + locationMode.Text + " · 间隔 " + pendingOptions.Interval + " ms · 步骤 " + engine.StepCount +
                    " · 轮数 " + (pendingOptions.Limit == 0 ? "不限" : pendingOptions.Limit.ToString()));
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidDataException) && !(ex is InvalidOperationException)) throw;
                Log.Info("无法开始：" + ex.Message);
                status.Text = "●  " + ex.Message; SetBusy(false);
            }
        }
        private void ToggleRecording()
        {
            if (recorder.Recording) { StopRecording(); return; }
            if (pending || engine.Running || capturing || targetCapturing) { status.Text = "●  请先停止运行或取点再录制"; return; }
            try
            {
                recorder.Start();
                recordButton.Text = "停止录制（" + HotkeyText(advanced.HotkeyStop) + "）";
                locationMode.SelectedIndex = 3;
                Log.Info("开始录制操作");
                status.Text = "●  录制中 · 请操作目标窗口，" + HotkeyText(advanced.HotkeyStop) + " 停止";
            }
            catch (Exception error)
            {
                Log.Error("无法开始录制", error);
                status.Text = "●  " + error.Message;
            }
        }

        private void StopRecording()
        {
            if (!recorder.Recording) return;
            List<MacroEntry> recorded = recorder.Stop();
            recordButton.Text = "录制操作…";
            if (recorded.Count == 0) { status.Text = "●  没有录到操作"; Log.Info("录制结束：没有记录到动作"); return; }
            if (advanced.RelativeCoordinates)
            {
                Point origin;
                if (TargetOrigin(out origin))
                {
                    int dx = -origin.X, dy = -origin.Y;
                    foreach (MacroEntry entry in recorded)
                    {
                        switch (entry.Kind)
                        {
                            case ActionKind.Click:
                            case ActionKind.Move:
                            case ActionKind.Scroll:
                            case ActionKind.Pixel:
                                entry.X += dx; entry.Y += dy;
                                break;
                            case ActionKind.Drag:
                                entry.X += dx; entry.Y += dy; entry.EndX += dx; entry.EndY += dy;
                                break;
                        }
                    }
                }
                else Log.Info("录制结束时目标窗口不可用，录制结果保留为屏幕坐标");
            }
            int room = ProfileStore.MaxPoints - macroEntries.Count;
            if (room <= 0) { status.Text = "●  宏步骤已满，无法追加录制结果"; return; }
            if (recorded.Count > room) recorded.RemoveRange(room, recorded.Count - room);
            macroEntries.AddRange(recorded);
            RefreshMacro(macroEntries.Count - 1);
            Log.Info("录制结束，追加 " + recorded.Count + " 步");
            status.Text = "●  已录制并追加 " + recorded.Count + " 个步骤";
        }

        // Keys the macro engine refuses to send, plus the live global hotkeys.
        private bool IsReservedRecordingKey(int key, bool control, bool alt, bool shift)
        {
            try { ActionCompiler.Validate(new MacroEntry { Kind = ActionKind.Key, KeyCode = key, Control = control, Alt = alt, Shift = shift }); }
            catch (InvalidDataException) { return true; }
            int[] hotkeys = { advanced.HotkeyStart, advanced.HotkeyCapture, advanced.HotkeyStop, advanced.HotkeyPause, advanced.HotkeyCapture2, advanced.HotkeyEmergency };
            int mods = (control ? 2 : 0) | (alt ? 1 : 0) | (shift ? 4 : 0);
            foreach (int packed in hotkeys)
                if ((packed & 0xFFFF) == key && ((packed >> 16) & 0xF) == mods) return true;
            return false;
        }

        internal static string HotkeyText(int packed)
        {
            return HotkeyBox.Describe(packed);
        }

        private void ApplyHotkeys()
        {
            if (!useHotkeys) { hotkeysReady = true; return; }
            if (Handle == IntPtr.Zero) return;
            for (int id = 1; id <= 6; id++) Native.UnregisterHotKey(Handle, id);
            bool toggle = RegisterHotkey(1, advanced.HotkeyStart);
            bool capture = RegisterHotkey(2, advanced.HotkeyCapture);
            bool stop = RegisterHotkey(3, advanced.HotkeyStop);
            bool pause = RegisterHotkey(4, advanced.HotkeyPause);
            bool add = RegisterHotkey(5, advanced.HotkeyCapture2);
            bool emergency = RegisterHotkey(6, advanced.HotkeyEmergency);
            hotkeysReady = toggle && stop;
            if (!toggle || !capture || !stop || !pause || !add || !emergency)
            {
                string failed = (!toggle ? HotkeyText(advanced.HotkeyStart) + " " : "") + (!capture ? HotkeyText(advanced.HotkeyCapture) + " " : "") +
                    (!stop ? HotkeyText(advanced.HotkeyStop) + " " : "") + (!pause ? HotkeyText(advanced.HotkeyPause) + " " : "") +
                    (!add ? HotkeyText(advanced.HotkeyCapture2) + " " : "") + (!emergency ? HotkeyText(advanced.HotkeyEmergency) + " " : "");
                if (lastHotkeyFailure != failed) Log.Info("快捷键注册失败：" + failed);
                lastHotkeyFailure = failed;
                hotkeyWarning = "快捷键被占用：" + failed + "，可在「更多设置 → 设置热键」更换，或关闭占用它的程序";
                start.Enabled = hotkeysReady;
            }
            else
            {
                hotkeyWarning = "";
                lastHotkeyFailure = "";
                start.Enabled = true;
            }
            UpdateHotkeyLabels();
        }

        private bool RegisterHotkey(int id, int packed)
        {
            int key = packed & 0xFFFF;
            if (key == 0) return true;
            uint mods = (uint)((packed >> 16) & 0xF);
            return Native.RegisterHotKey(Handle, id, Native.NoRepeat | mods, (uint)key);
        }

        private void UpdateHotkeyLabels()
        {
            if (start == null) return;
            SetBusy(uiBusy);
            if (stopButton != null) stopButton.Text = "停止   " + HotkeyText(advanced.HotkeyStop);
            if (pauseButton != null) pauseButton.Text = (engine.Paused ? "恢复   " : "暂停   ") + HotkeyText(advanced.HotkeyPause);
            if (trayStart != null) trayStart.Text = "开始（" + HotkeyText(advanced.HotkeyStart) + "）";
            if (trayPause != null) trayPause.Text = "暂停/恢复（" + HotkeyText(advanced.HotkeyPause) + "）";
            if (trayStop != null) trayStop.Text = "停止（" + HotkeyText(advanced.HotkeyStop) + "）";
            if (hint != null) hint.Text = "启动前倒数 " + advanced.StartupDelay + " 秒 · 鼠标移至主屏左上角可紧急停止";
        }

        private void PrepareAdvancedRun(ClickOptions options, List<string> problems)
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
                        string error = PositionError(StoredToScreen(new Point(entry.X, entry.Y)));
                        if (error != null) { problems.Add("步骤 " + (i + 1) + "：" + error + "。"); continue; }
                    }
                    selected.Add(entry); numbers.Add(i + 1);
                }
                if (selected.Count == 0) problems.Add("请添加并勾选至少一个宏步骤。");
                options.Actions = ProfileStore.Copy(selected).ToArray(); options.SourceNumbers = numbers.ToArray();
                if (loopStart.Value > 0 && loopCount.Value > 1)
                {
                    options.LoopStart = numbers.IndexOf((int)loopStart.Value);
                    options.LoopEnd = numbers.IndexOf((int)loopEnd.Value);
                    options.LoopCount = (int)loopCount.Value;
                    if (options.LoopStart < 0 || options.LoopEnd < options.LoopStart)
                        problems.Add("局部循环的起点、终点必须已勾选且顺序正确。");
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
                    problems.Add("未找到唯一的目标窗口，请在「更多设置」里重新绑定。");
                options.TargetWindow = targetWindow; options.TargetProcessId = targetProcessId;
            }
        }
        private void UpdateRunDisplay()
        {
            if (pauseButton == null) return;
            pauseButton.Enabled = engine.Running;
            pauseButton.Text = (engine.Paused ? "恢复   " : "暂停   ") + HotkeyText(advanced.HotkeyPause);
            if (engine.Running)
            {
                string limitText = pendingOptions.Limit == 0 ? "不限" : pendingOptions.Limit.ToString();
                runDetail.Text = (engine.Paused ? "已暂停 · " : "") + engine.Phase + " · 剩余 " + engine.Remaining + " ms\n已完成轮数 " + engine.Rounds + " / " + limitText +
                    (engine.Repetitions > 0 ? " · 点内动作 " + engine.Repetition + " / " + engine.Repetitions : "");
            }
            else if (!pending && !capturing && !targetCapturing) { runDetail.Text = "已完成 " + engine.Rounds + " 轮 · " + engine.Reason; runDetail.ForeColor = Muted; }
            // A hotkey conflict stays visible until it is fixed; the run text must not hide it.
            if (hotkeyWarning.Length > 0 && !engine.Running)
            { runDetail.Text = hotkeyWarning; runDetail.ForeColor = Color.FromArgb(168, 58, 36); }
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
                trayPause.Text = (engine.Paused ? "恢复（" : "暂停（") + HotkeyText(advanced.HotkeyPause) + "）";
                trayStop.Enabled = engine.Running || pending || capturing || targetCapturing;
                tray.Text = AppInfo.TrayTitle + " · " + (engine.Running ? engine.Paused ? "已暂停" : "运行中 · 第 " + current + " 项" : "已停止");
            }
        }
        // A pause or stop the user cannot see (window hidden or behind another app)
        // also gets a balloon. Logged either way, so support can read the sequence.
        private void Notify(string text)
        {
            Log.Info("提示：" + text);
            try
            {
                if (tray == null) return;
                if (Visible && ContainsFocus) return;
                tray.ShowBalloonTip(4000, AppInfo.ShortName, text, ToolTipIcon.Info);
            }
            catch (Exception error) { Log.Error("托盘提示失败", error); }
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
            if (targetLabel == null) return;
            string mode = advanced.RelativeCoordinates ? " · 坐标相对目标窗口" : "";
            targetLabel.Text = advanced.ProtectTarget ? "目标保护：" + (advanced.TargetTitle.Length == 0 ? "尚未绑定" : advanced.TargetTitle) + mode : "目标保护：未启用" + mode;
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
            BindTargetWindow(Native.GetForegroundWindow());
            return true;
        }

        // Shared by the 3-second capture and the window list.
        private bool BindTargetWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || window == Handle || !Native.IsWindow(window))
            {
                Log.Info("绑定目标窗口失败：窗口无效或就是连点器自己");
                status.Text = "●  没有选到有效的外部窗口，请重试";
                return false;
            }
            string title = Native.WindowTitle(window), process = Native.ProcessName(window);
            if (title.Length == 0 || process.Length == 0)
            {
                Log.Info("绑定目标窗口失败：窗口没有标题或进程名");
                status.Text = "●  没有选到有效的外部窗口，请重试";
                return false;
            }
            targetWindow = window;
            Native.GetWindowThreadProcessId(window, out targetProcessId);
            advanced.TargetTitle = title; advanced.TargetProcess = process; advanced.ProtectTarget = true;
            markersDirty = true;
            Log.Info("已绑定目标窗口：" + process + " · " + title);
            UpdateTargetLabel();
            status.Text = "●  已绑定目标窗口并启用保护";
            return true;
        }

        // Picking from a list is steadier than racing a 3-second countdown.
        private void PickTargetWindow()
        {
            using (Form dialog = new Form
            {
                Text = "选择目标窗口", ClientSize = new Size(680, 470), Font = UiFont(9.5F, FontStyle.Regular),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                ListView list = new ListView { Bounds = new Rectangle(23, 20, 634, 340), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
                list.Columns.Add("程序", 190);
                list.Columns.Add("窗口标题", 420);
                EventHandler fill = delegate
                {
                    list.BeginUpdate();
                    list.Items.Clear();
                    foreach (IntPtr window in Native.TaskWindows())
                    {
                        string title = Native.WindowTitle(window), process = Native.ProcessName(window);
                        if (title.Length == 0 || process.Length == 0) continue;
                        ListViewItem item = new ListViewItem(process) { Tag = window };
                        item.SubItems.Add(title);
                        list.Items.Add(item);
                    }
                    list.EndUpdate();
                };
                fill(null, EventArgs.Empty);
                dialog.Controls.Add(list);
                SmallButton(dialog, "刷新", 23, 380, 100, delegate { fill(null, EventArgs.Empty); });
                Button okay = SmallButton(dialog, "绑定所选窗口", 400, 380, 150, delegate { }); okay.DialogResult = DialogResult.OK;
                Button cancel = SmallButton(dialog, "取消", 560, 380, 97, delegate { }); cancel.DialogResult = DialogResult.Cancel;
                dialog.AcceptButton = okay; dialog.CancelButton = cancel;
                if (ShowModal(delegate { return dialog.ShowDialog(this); }) != DialogResult.OK) return;
                if (list.SelectedItems.Count == 0) { status.Text = "●  没有选择窗口"; return; }
                BindTargetWindow((IntPtr)list.SelectedItems[0].Tag);
            }
        }

        private static bool IsElevated
        {
            get
            {
                using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        // SendInput cannot reach elevated windows; this relaunches the app elevated and
        // waits for this instance to release the single-instance mutex (--relaunch).
        private void RestartElevated()
        {
            if (IsElevated) { status.Text = "●  当前已经是管理员权限"; return; }
            if (engine.Running || pending) { status.Text = "●  请先停止运行再切换权限"; return; }
            if (!SaveCurrentProfile()) return;
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath)
                { UseShellExecute = true, Verb = "runas", Arguments = "--relaunch" };
                System.Diagnostics.Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                Log.Info("取消以管理员身份重启：" + error.Message);
                status.Text = "●  已取消以管理员身份重启";
                return;
            }
            catch (Exception error)
            {
                Log.Error("以管理员身份重启失败", error);
                status.Text = "●  " + error.Message;
                return;
            }
            Log.Info("已请求以管理员身份重启");
            RequestExit();
        }

        private static void SetPortable(bool enabled)
        {
            if (enabled) File.WriteAllText(AppPaths.MarkerPath, "portable" + Environment.NewLine);
            else if (File.Exists(AppPaths.MarkerPath)) File.Delete(AppPaths.MarkerPath);
        }

        // Switches every stored point between screen coordinates and offsets inside the
        // bound target window's client area.
        private bool ApplyCoordinateMode(bool relative)
        {
            Point origin;
            if (!TargetOrigin(out origin))
            {
                Log.Info("切换坐标模式失败：没有可用的目标窗口");
                ShowModal(delegate
                {
                    return MessageBox.Show(this, "切换坐标模式需要先绑定一个目标窗口。\n请在「更多设置」里选择或绑定目标窗口后再试。",
                        "坐标模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
                return false;
            }
            int dx = relative ? -origin.X : origin.X;
            int dy = relative ? -origin.Y : origin.Y;
            foreach (PositionEntry point in positions)
            {
                point.X += dx;
                point.Y += dy;
            }
            if (selectedPoint.HasValue) selectedPoint = new Point(selectedPoint.Value.X + dx, selectedPoint.Value.Y + dy);
            foreach (MacroEntry entry in macroEntries)
            {
                switch (entry.Kind)
                {
                    case ActionKind.Click:
                    case ActionKind.Move:
                    case ActionKind.Scroll:
                    case ActionKind.Pixel:
                        entry.X += dx; entry.Y += dy;
                        break;
                    case ActionKind.Drag:
                        entry.X += dx; entry.Y += dy; entry.EndX += dx; entry.EndY += dy;
                        break;
                }
            }
            advanced.RelativeCoordinates = relative;
            RefreshPositions(positions.Count > 0 ? 0 : -1);
            RefreshMacro(macroEntries.Count > 0 ? 0 : -1);
            UpdateTargetLabel();
            SyncMarkers();
            Log.Info("坐标模式：" + (relative ? "相对目标窗口" : "屏幕绝对坐标"));
            status.Text = "●  坐标模式已切换为" + (relative ? "相对目标窗口" : "屏幕绝对坐标");
            return true;
        }

        internal static CheckBox OptionCheck(Control parent, string text, int x, int y, int width, bool value)
        {
            CheckBox check = new CheckBox { Text = text, Checked = value, Bounds = new Rectangle(x, y, width, 36) };
            parent.Controls.Add(check); return check;
        }

        // The dialog itself lives in OptionsDialog so the layout test can build it.
        private void ShowOptions()
        {
            bool originalStartup = ReadLoginStartup();
            int oldStart = advanced.HotkeyStart, oldCapture = advanced.HotkeyCapture, oldStop = advanced.HotkeyStop;
            int oldPause = advanced.HotkeyPause, oldCapture2 = advanced.HotkeyCapture2, oldEmergency = advanced.HotkeyEmergency;
            using (OptionsDialog dialog = new OptionsDialog(advanced, AppPaths.Portable, originalStartup))
            {
                dialog.DiagnosticsRequested += delegate { ExportDiagnostics(); };
                DialogResult result = ShowModal(delegate { return dialog.ShowDialog(this); });
                if (result != DialogResult.OK && result != DialogResult.Retry && result != DialogResult.Yes && result != DialogResult.Abort) return;
                advanced.StartupDelay = dialog.StartupDelayValue;
                advanced.ProtectTarget = dialog.ProtectTargetValue;
                advanced.HighPrecision = dialog.HighPrecisionValue;
                advanced.TrayOnMinimize = dialog.TrayOnMinimizeValue;
                advanced.StartMinimized = dialog.StartMinimizedValue;
                advanced.ScheduleEnabled = dialog.ScheduleEnabledValue;
                advanced.ScheduleHour = dialog.ScheduleHourValue;
                advanced.ScheduleMinute = dialog.ScheduleMinuteValue;
                advanced.RunMinutes = dialog.RunMinutesValue;
                advanced.JitterInterval = dialog.JitterIntervalValue;
                advanced.JitterPosition = dialog.JitterPositionValue;
                bool hotkeysChanged = oldStart != dialog.HotkeyStartValue || oldCapture != dialog.HotkeyCaptureValue || oldStop != dialog.HotkeyStopValue ||
                    oldPause != dialog.HotkeyPauseValue || oldCapture2 != dialog.HotkeyCapture2Value || oldEmergency != dialog.HotkeyEmergencyValue;
                advanced.HotkeyStart = dialog.HotkeyStartValue; advanced.HotkeyCapture = dialog.HotkeyCaptureValue; advanced.HotkeyStop = dialog.HotkeyStopValue;
                advanced.HotkeyPause = dialog.HotkeyPauseValue; advanced.HotkeyCapture2 = dialog.HotkeyCapture2Value; advanced.HotkeyEmergency = dialog.HotkeyEmergencyValue;
                if (dialog.LoginStartupValue != originalStartup)
                {
                    try { SetLoginStartup(dialog.LoginStartupValue); Log.Info("登录启动已" + (dialog.LoginStartupValue ? "启用" : "关闭")); }
                    catch (Exception ex)
                    {
                        if (!IsFileError(ex)) throw;
                        Log.Error("登录启动设置失败", ex);
                        ShowModal(delegate { return MessageBox.Show(this, "登录启动设置失败：" + ex.Message, "更多设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                    }
                }
                if (dialog.PortableModeValue != AppPaths.Portable)
                {
                    try
                    {
                        SetPortable(dialog.PortableModeValue);
                        status.Text = "●  便携模式已" + (dialog.PortableModeValue ? "启用" : "关闭") + "，重启程序后生效";
                    }
                    catch (Exception ex)
                    {
                        if (!IsFileError(ex)) throw;
                        Log.Error("切换便携模式失败", ex);
                        ShowModal(delegate { return MessageBox.Show(this, "无法修改便携模式：" + ex.Message, "更多设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                    }
                }
                if (dialog.RelativeCoordinatesValue != advanced.RelativeCoordinates) ApplyCoordinateMode(dialog.RelativeCoordinatesValue);
                if (hotkeysChanged) ApplyHotkeys();
                UpdateTargetLabel();
                MarkDirty();
                if (result == DialogResult.Abort) { RestartElevated(); return; }
                if (result == DialogResult.Yes) { PickTargetWindow(); return; }
                if (result == DialogResult.Retry) { BeginTargetCapture(); return; }
                status.Text = "●  设置已应用；方案切换或退出时保存";
            }
        }
        private string DiagnosticText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("轻点 " + AppInfo.Version);
            text.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("系统: " + Environment.OSVersion.VersionString + " · CLR " + Environment.Version);
            text.AppendLine("程序: " + Application.ExecutablePath);
            text.AppendLine("数据目录: " + AppPaths.DataFolder + (AppPaths.Portable ? "（便携模式）" : ""));
            foreach (Screen screen in Screen.AllScreens)
                text.AppendLine("屏幕: " + screen.DeviceName + " 范围 " + screen.Bounds + " 工作区 " + screen.WorkingArea + (screen.Primary ? "（主屏）" : ""));
            text.AppendLine("热键: 开始/停止 " + HotkeyText(advanced.HotkeyStart) + " · 取点 " + HotkeyText(advanced.HotkeyCapture) +
                " · 停止 " + HotkeyText(advanced.HotkeyStop) + " · 暂停 " + HotkeyText(advanced.HotkeyPause) +
                " · 备用取点 " + HotkeyText(advanced.HotkeyCapture2) + " · 紧急 " + HotkeyText(advanced.HotkeyEmergency));
            text.AppendLine("方案: " + library.Profiles.Count + " 个，当前 " + CurrentProfile.Name);
            text.AppendLine("模式: " + locationMode.Text + " · 引擎: " + (engine.Running ? "运行中" : "停止") + " · " + engine.Reason);
            text.AppendLine("录制: " + (recorder.Recording ? "进行中" : "未进行"));
            if (lastReport != null) text.AppendLine("上次运行: " + lastReport.Summary);
            return text.ToString();
        }

        private void ExportDiagnostics()
        {
            string name = "轻点诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            Form owner = ActiveForm ?? this;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "诊断包 (*.zip)|*.zip", Title = "导出诊断包", FileName = name, DefaultExt = "zip", AddExtension = true, OverwritePrompt = true })
            {
                if (ShowModal(delegate { return dialog.ShowDialog(owner); }) != DialogResult.OK) return;
                try
                {
                    Diagnostics.Export(dialog.FileName, DiagnosticText(), profilesPath, Log.Folder);
                    Log.Info("已导出诊断包");
                    status.Text = "●  诊断包已导出：" + dialog.FileName;
                }
                catch (Exception error)
                {
                    if (!IsFileError(error)) throw;
                    Log.Error("导出诊断包失败", error);
                    ShowModal(delegate { return MessageBox.Show(this, "导出失败：\n" + error.Message, "诊断包", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                }
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
            recorder.Dispose();
        }
    }
}
