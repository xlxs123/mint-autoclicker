using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace MintClicker
{
    // Records real mouse clicks, wheel moves and key presses with low-level hooks and
    // turns them into ordinary macro steps. Only physical, non-injected input from other
    // windows is kept, so the clicker never records its own output or its own UI.
    internal sealed class Recorder : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208;
        private const uint LLMHF_INJECTED = 0x00000001, LLKHF_INJECTED = 0x00000010;
        private const int WM_QUIT = 0x0012;
        private const int MaxSteps = 1000;
        private const long DoubleClickWindow = 400;
        private const int MinGap = 40;
        private const int DragThreshold = 6;

        internal enum RecordedEventKind { Click, Key, Scroll, Drag }
        internal struct RecordedEvent
        {
            internal RecordedEventKind Kind;
            internal int X, Y, Button, Wheel, Key;
            internal int EndX, EndY, Duration;
            internal bool Control, Alt, Shift;
            internal long Time;
        }

        private readonly Func<IntPtr> ownWindow;
        private readonly Func<int, bool, bool, bool, bool> ignoredKey;
        private readonly object sync = new object();
        private readonly List<RecordedEvent> events = new List<RecordedEvent>();
        private readonly Stopwatch clock = new Stopwatch();
        private Thread thread;
        private uint threadId;
        private IntPtr mouseHook, keyboardHook;
        private HookProc mouseProc, keyboardProc;
        private ManualResetEvent ready;
        private bool failed;
        private volatile bool recording;
        private bool mouseDownPending, dragMoved;
        private int downButton;
        private Point downPoint;
        private long downTime;

        internal Recorder(Func<IntPtr> ownWindow, Func<int, bool, bool, bool, bool> ignoredKey)
        {
            this.ownWindow = ownWindow;
            this.ignoredKey = ignoredKey;
        }

        internal bool Recording { get { return recording; } }
        internal int Count { get { lock (sync) return events.Count; } }

        internal void Start()
        {
            if (recording) return;
            lock (sync) events.Clear();
            clock.Restart();
            ready = new ManualResetEvent(false);
            failed = false;
            mouseProc = new HookProc(MouseCallback);
            keyboardProc = new HookProc(KeyboardCallback);
            recording = true;
            thread = new Thread(HookLoop) { IsBackground = true, Name = "Recorder" };
            thread.Start();
            if (!ready.WaitOne(3000) || failed)
            {
                recording = false;
                Stop();
                throw new InvalidOperationException("无法安装键鼠钩子（可能被安全软件拦截）。");
            }
        }

        internal List<MacroEntry> Stop()
        {
            recording = false;
            Thread current = thread;
            if (current != null && current.IsAlive)
            {
                if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                current.Join(2000);
            }
            thread = null;
            return Build();
        }

        private void HookLoop()
        {
            threadId = GetCurrentThreadId();
            IntPtr module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            failed = mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero;
            if (ready != null) ready.Set();
            if (failed)
            {
                // Release the half that did install, otherwise it would stay hooked.
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                mouseHook = keyboardHook = IntPtr.Zero;
                return;
            }
            Msg message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            UnhookWindowsHookEx(mouseHook);
            UnhookWindowsHookEx(keyboardHook);
            mouseHook = keyboardHook = IntPtr.Zero;
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && recording) RecordMouse(wParam.ToInt32(), lParam);
            }
            catch (Exception) { }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int message = wParam.ToInt32();
                if (code >= 0 && recording && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)) RecordKey(lParam);
            }
            catch (Exception) { }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void RecordMouse(int message, IntPtr lParam)
        {
            MouseHookData data = (MouseHookData)Marshal.PtrToStructure(lParam, typeof(MouseHookData));
            if ((data.flags & LLMHF_INJECTED) != 0) return;
            Point point = new Point(data.pt.x, data.pt.y);
            if (message == WM_MOUSEMOVE)
            {
                // Tracking only: a press that travels far enough becomes a drag.
                if (!mouseDownPending || dragMoved) return;
                if (Math.Abs(point.X - downPoint.X) <= DragThreshold && Math.Abs(point.Y - downPoint.Y) <= DragThreshold) return;
                dragMoved = true;
                return;
            }
            // Clicks on the clicker itself (the record / stop button) are not part of the macro.
            if (Native.IsOwnWindow(point, ownWindow()))
            {
                if (message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP) ClearPress();
                return;
            }
            switch (message)
            {
                case WM_LBUTTONDOWN: BeginPress(0, point); break;
                case WM_RBUTTONDOWN: BeginPress(1, point); break;
                case WM_MBUTTONDOWN: BeginPress(2, point); break;
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                case WM_MBUTTONUP:
                    EndPress(point);
                    break;
                case WM_MOUSEWHEEL:
                    int notches = (short)((data.mouseData >> 16) & 0xFFFF) / 120;
                    if (notches == 0) return;
                    Add(new RecordedEvent { Kind = RecordedEventKind.Scroll, X = point.X, Y = point.Y, Wheel = Math.Max(-100, Math.Min(100, notches)) });
                    break;
            }
        }

        private void BeginPress(int button, Point point)
        {
            mouseDownPending = true;
            downButton = button;
            downPoint = point;
            downTime = clock.ElapsedMilliseconds;
            dragMoved = false;
        }

        private void EndPress(Point point)
        {
            if (!mouseDownPending) return;
            bool moved = dragMoved;
            mouseDownPending = false;
            dragMoved = false;
            if (moved)
            {
                long duration = Math.Max(1, clock.ElapsedMilliseconds - downTime);
                AddAt(new RecordedEvent
                {
                    Kind = RecordedEventKind.Drag, X = downPoint.X, Y = downPoint.Y,
                    EndX = point.X, EndY = point.Y, Button = downButton,
                    Duration = (int)Math.Min(duration, 60000)
                }, downTime);
            }
            else
            {
                AddAt(new RecordedEvent { Kind = RecordedEventKind.Click, X = downPoint.X, Y = downPoint.Y, Button = downButton }, downTime);
            }
        }

        private void ClearPress()
        {
            mouseDownPending = false;
            dragMoved = false;
        }

        private void RecordKey(IntPtr lParam)
        {
            KeyboardHookData data = (KeyboardHookData)Marshal.PtrToStructure(lParam, typeof(KeyboardHookData));
            if ((data.flags & LLKHF_INJECTED) != 0) return;
            if (Native.GetForegroundWindow() == ownWindow()) return;
            int key = (int)data.vkCode;
            if (key == 16 || key == 17 || key == 18) return;
            bool control = (Native.GetAsyncKeyState(17) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(18) & 0x8000) != 0;
            bool shift = (Native.GetAsyncKeyState(16) & 0x8000) != 0;
            if (ignoredKey != null && ignoredKey(key, control, alt, shift)) return;
            Add(new RecordedEvent { Kind = RecordedEventKind.Key, Key = key, Control = control, Alt = alt, Shift = shift });
        }

        private void Add(RecordedEvent item)
        {
            AddAt(item, clock.ElapsedMilliseconds);
        }

        private void AddAt(RecordedEvent item, long time)
        {
            lock (sync)
            {
                if (events.Count >= MaxSteps) return;
                item.Time = time;
                events.Add(item);
            }
        }

        // Turns raw events into steps: small gaps become waits, fast repeated clicks on
        // the same spot collapse into one double-click step. Static so the test suite can
        // feed synthetic events without installing hooks.
        internal static List<MacroEntry> Convert(List<RecordedEvent> events)
        {
            List<MacroEntry> steps = new List<MacroEntry>();
            MacroEntry lastClick = null;
            long lastTime = 0;
            foreach (RecordedEvent item in events)
            {
                if (steps.Count >= MaxSteps) break;
                if (item.Kind == RecordedEventKind.Drag)
                {
                    AddWait(steps, item.Time - lastTime);
                    steps.Add(new MacroEntry
                    {
                        Kind = ActionKind.Drag, X = item.X, Y = item.Y, EndX = item.EndX, EndY = item.EndY,
                        Button = item.Button, Duration = item.Duration, DelayAfter = 0
                    });
                    lastClick = null;
                    lastTime = item.Time;
                    continue;
                }
                if (item.Kind == RecordedEventKind.Click && lastClick != null && lastClick.Button == item.Button &&
                    Math.Abs(lastClick.X - item.X) <= 2 && Math.Abs(lastClick.Y - item.Y) <= 2 && item.Time - lastTime <= DoubleClickWindow)
                {
                    lastClick.DoubleClick = true;
                    lastTime = item.Time;
                    lastClick = null;
                    continue;
                }
                AddWait(steps, item.Time - lastTime);
                if (item.Kind == RecordedEventKind.Click)
                {
                    MacroEntry click = new MacroEntry { Kind = ActionKind.Click, X = item.X, Y = item.Y, Button = item.Button, DelayAfter = 0 };
                    steps.Add(click);
                    lastClick = click;
                }
                else if (item.Kind == RecordedEventKind.Key)
                {
                    steps.Add(new MacroEntry { Kind = ActionKind.Key, KeyCode = item.Key, Control = item.Control, Alt = item.Alt, Shift = item.Shift, DelayAfter = 0 });
                    lastClick = null;
                }
                else
                {
                    steps.Add(new MacroEntry { Kind = ActionKind.Scroll, X = item.X, Y = item.Y, Wheel = item.Wheel, DelayAfter = 0 });
                    lastClick = null;
                }
                lastTime = item.Time;
            }
            return steps;
        }

        private List<MacroEntry> Build()
        {
            List<RecordedEvent> copy;
            lock (sync)
            {
                copy = new List<RecordedEvent>(events);
                events.Clear();
            }
            return Convert(copy);
        }

        private static void AddWait(List<MacroEntry> steps, long gap)
        {
            if (steps.Count == 0 || gap < MinGap || steps.Count >= MaxSteps) return;
            steps.Add(new MacroEntry { Kind = ActionKind.Wait, Duration = (int)Math.Min(gap, 3600000), DelayAfter = 0 });
        }

        public void Dispose()
        {
            Stop();
            if (ready != null) { ready.Dispose(); ready = null; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHookData
        {
            internal NativePoint pt;
            internal uint mouseData;
            internal uint flags;
            internal uint time;
            internal UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookData
        {
            internal uint vkCode;
            internal uint scanCode;
            internal uint flags;
            internal uint time;
            internal UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            internal IntPtr window;
            internal uint message;
            internal UIntPtr wParam;
            internal IntPtr lParam;
            internal uint time;
            internal NativePoint pt;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg message, IntPtr window, uint minimum, uint maximum);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg message);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    }
}
