using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace MintClicker
{
    internal static class Native
    {
        internal const int WM_HOTKEY = 0x0312;
        internal const uint NoRepeat = 0x4000;
        private static readonly HashSet<IntPtr> markerWindows = new HashSet<IntPtr>();
        internal static void RegisterMarker(IntPtr handle) { lock (markerWindows) markerWindows.Add(handle); }
        internal static void UnregisterMarker(IntPtr handle) { lock (markerWindows) markerWindows.Remove(handle); }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { internal int left, top, right, bottom; }
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort key, scan;
            internal uint flags, time;
            internal UIntPtr extraInfo;
        }
        // MOUSEINPUT is the largest member of the native INPUT union.
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyboardInput keyboard; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PointNative { public int x, y; }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out PointNative point);
        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(PointNative point);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        private delegate bool EnumWindowProc(IntPtr window, IntPtr state);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);

        internal static string WindowTitle(IntPtr window)
        {
            StringBuilder text = new StringBuilder(1024);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }
        internal static string ProcessName(IntPtr window)
        {
            uint id;
            GetWindowThreadProcessId(window, out id);
            try { using (Process process = Process.GetProcessById((int)id)) return process.ProcessName; }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (Win32Exception) { return ""; }
        }
        internal static bool IsTargetForeground(IntPtr window, uint processId)
        {
            uint current;
            GetWindowThreadProcessId(window, out current);
            return IsWindow(window) && current == processId && GetForegroundWindow() == window;
        }
        internal static IntPtr FindTarget(string title, string process)
        {
            IntPtr result = IntPtr.Zero;
            int matches = 0;
            EnumWindows(delegate(IntPtr window, IntPtr state)
            {
                if (IsWindowVisible(window) && WindowTitle(window) == title && ProcessName(window) == process)
                { result = window; matches++; }
                return matches < 2;
            }, IntPtr.Zero);
            return matches == 1 ? result : IntPtr.Zero;
        }

        internal static Point CursorPoint()
        {
            PointNative point;
            if (!GetCursorPos(out point)) throw new InvalidOperationException("无法读取鼠标位置。");
            return new Point(point.x, point.y);
        }

        internal static bool IsOwnWindow(Point point, IntPtr mainWindow)
        {
            IntPtr hit = WindowFromPoint(new PointNative { x = point.X, y = point.Y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2);
            if (mainWindow == IntPtr.Zero) return false;
            if (root == mainWindow) return true;
            lock (markerWindows)
            {
                // A transparent overlay must not mask protection of the main window below it.
                if (!markerWindows.Contains(root)) return false;
            }
            WindowRect rect;
            return IsWindowVisible(mainWindow) && !IsIconic(mainWindow) && GetWindowRect(mainWindow, out rect)
                && point.X >= rect.left && point.X < rect.right && point.Y >= rect.top && point.Y < rect.bottom;
        }

        internal static Input Mouse(uint flags, int x, int y)
        {
            return new Input { type = 0, data = new InputUnion { mouse = new MouseInput { flags = flags, dx = x, dy = y } } };
        }

        private static Input AbsoluteMove(Point point, Rectangle desktop)
        {
            int x = (int)Math.Round((point.X - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1));
            int y = (int)Math.Round((point.Y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1));
            return Mouse(0x0001u | 0x8000u | 0x4000u, x, y);
        }
        private static void SendChecked(Input[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new InvalidOperationException("输入未完整发送，请检查目标程序权限。");
        }
        internal static void Move(Point point, Rectangle desktop) { SendChecked(new[] { AbsoluteMove(point, desktop) }); }
        internal static void Scroll(Point point, int notches, Rectangle desktop)
        {
            Input wheel = Mouse(0x0800u, 0, 0);
            wheel.data.mouse.mouseData = unchecked((uint)(notches * 120));
            SendChecked(new[] { AbsoluteMove(point, desktop), wheel });
        }
        private static Input Key(int code, bool up)
        {
            bool extended = (code >= 33 && code <= 46) || code == 111 || code == 144;
            return new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { key = (ushort)code, flags = (up ? 2u : 0u) | (extended ? 1u : 0u) } } };
        }
        internal static void KeyPress(int code, bool control, bool alt, bool shift)
        {
            List<int> keys = new List<int>();
            if (control) keys.Add(17);
            if (alt) keys.Add(18);
            if (shift) keys.Add(16);
            keys.Add(code);
            foreach (int key in keys)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("宏按键仍被手动按住，请松开后重新开始。");
            List<Input> batch = new List<Input>(), releases = new List<Input>();
            foreach (int key in keys) batch.Add(Key(key, false));
            for (int i = keys.Count - 1; i >= 0; i--) { Input up = Key(keys[i], true); batch.Add(up); releases.Add(up); }
            if (SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf(typeof(Input))) != batch.Count)
            {
                SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf(typeof(Input)));
                throw new InvalidOperationException("键盘输入未完整发送，已尝试释放按键。请检查权限。");
            }
        }

        internal static void Click(int button, bool twice, Point? fixedPoint, Rectangle desktop)
        {
            uint down = button == 1 ? 0x0008u : button == 2 ? 0x0020u : 0x0002u;
            uint up = button == 1 ? 0x0010u : button == 2 ? 0x0040u : 0x0004u;
            int offset = fixedPoint.HasValue ? 1 : 0;
            Input[] inputs = new Input[offset + (twice ? 4 : 2)];
            if (fixedPoint.HasValue)
            {
                // Absolute input uses the complete virtual desktop, including negative coordinates.
                int x = (int)Math.Round((fixedPoint.Value.X - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1));
                int y = (int)Math.Round((fixedPoint.Value.Y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1));
                inputs[0] = Mouse(0x0001u | 0x8000u | 0x4000u, x, y);
            }
            for (int i = offset; i < inputs.Length; i += 2)
            {
                inputs[i] = Mouse(down, 0, 0);
                inputs[i + 1] = Mouse(up, 0, 0);
            }
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                // Release our selected button if Windows accepted only part of the batch.
                SendInput(1, new[] { Mouse(up, 0, 0) }, Marshal.SizeOf(typeof(Input)));
                throw new InvalidOperationException("点击未完整发送（错误 " + error + "）。请检查目标程序权限。");
            }
        }
    }
}
