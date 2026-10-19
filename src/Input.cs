using System;
using System.Drawing;
using System.Windows.Forms;

namespace MintClicker
{
    // Everything the engine touches outside its own logic. Production uses NativeInput;
    // the test suite substitutes a fake that records calls and feeds fake screen state.
    internal interface IInput
    {
        void Click(int button, bool twice, Point? fixedPoint, Rectangle desktop);
        void Drag(int button, Point from, Point to, int milliseconds, Rectangle desktop);
        void Move(Point point, Rectangle desktop);
        void Scroll(Point point, int notches, Rectangle desktop);
        void KeyPress(int code, bool control, bool alt, bool shift);
        Point CursorPoint();
        Color Pixel(Point point);
        short KeyState(int key);
        IntPtr ForegroundWindow();
        bool IsOwnWindow(Point point, IntPtr mainWindow);
        bool IsOnScreen(Point point);
        bool IsTargetForeground(IntPtr window, uint processId);
        bool ClientOrigin(IntPtr window, out Point origin);
        bool ClientArea(IntPtr window, out Rectangle area);
        int[] CaptureScreen(Rectangle area);
        void BeginPrecision();
        void EndPrecision();
    }

    internal sealed class NativeInput : IInput
    {
        internal static readonly NativeInput Instance = new NativeInput();

        public void Click(int button, bool twice, Point? fixedPoint, Rectangle desktop) { Native.Click(button, twice, fixedPoint, desktop); }
        public void Drag(int button, Point from, Point to, int milliseconds, Rectangle desktop) { Native.Drag(button, from, to, milliseconds, desktop); }
        public void Move(Point point, Rectangle desktop) { Native.Move(point, desktop); }
        public void Scroll(Point point, int notches, Rectangle desktop) { Native.Scroll(point, notches, desktop); }
        public void KeyPress(int code, bool control, bool alt, bool shift) { Native.KeyPress(code, control, alt, shift); }
        public Point CursorPoint() { return Native.CursorPoint(); }
        public Color Pixel(Point point) { return Native.Pixel(point); }
        public short KeyState(int key) { return Native.GetAsyncKeyState(key); }
        public IntPtr ForegroundWindow() { return Native.GetForegroundWindow(); }

        public bool IsOwnWindow(Point point, IntPtr mainWindow) { return Native.IsOwnWindow(point, mainWindow); }

        public bool IsOnScreen(Point point)
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.Bounds.Contains(point)) return true;
            return false;
        }

        public bool IsTargetForeground(IntPtr window, uint processId) { return Native.IsTargetForeground(window, processId); }
        public bool ClientOrigin(IntPtr window, out Point origin) { return Native.ClientOrigin(window, out origin); }
        public bool ClientArea(IntPtr window, out Rectangle area) { return Native.ClientArea(window, out area); }
        public int[] CaptureScreen(Rectangle area) { return Native.CaptureScreen(area); }
        public void BeginPrecision() { Native.BeginPrecision(); }
        public void EndPrecision() { Native.EndPrecision(); }
    }
}
