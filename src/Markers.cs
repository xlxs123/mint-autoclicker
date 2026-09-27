using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MintClicker
{
    internal static class PointBadge
    {
        internal static Color ColorFor(int button)
        {
            return button == 1 ? Color.FromArgb(22, 151, 82) : button == 2 ? Color.FromArgb(220, 53, 69) : button == 3 ? Color.FromArgb(101, 117, 110) : Color.FromArgb(36, 111, 232);
        }

        internal static string ButtonName(int button) { return button == 1 ? "右键" : button == 2 ? "中键" : "左键"; }

        internal static void Draw(Graphics graphics, Rectangle bounds, int number, int button, bool active = false)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle circle = Rectangle.Inflate(bounds, -2, -2);
            using (SolidBrush fill = new SolidBrush(ColorFor(button))) graphics.FillEllipse(fill, circle);
            using (Pen border = new Pen(Color.White, Math.Max(1, bounds.Width / 20f))) graphics.DrawEllipse(border, circle);
            if (active) using (Pen highlight = new Pen(Color.FromArgb(255, 185, 32), Math.Max(3, bounds.Width / 9f))) graphics.DrawEllipse(highlight, circle);
            string text = number.ToString();
            float ratio = text.Length <= 2 ? .46f : text.Length == 3 ? .36f : .28f;
            using (Font font = new Font("Segoe UI", bounds.Height * ratio, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(text, font, Brushes.White, bounds, format);
        }

        internal static Bitmap Image(int number, int button, int size)
        {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                Draw(graphics, new Rectangle(0, 0, size, size), number, button);
            }
            return bitmap;
        }
    }

    internal sealed class MarkerWindow : Form
    {
        private int number, button;
        private bool highlighted;
        internal int Number { get { return number; } }
        internal void Highlight(bool value) { if (highlighted == value) return; highlighted = value; if (IsHandleCreated) RenderLayered(); }
        private bool editable, dragging;
        private Point dragCursor, dragOrigin;
        private Action<int, Point> moved;
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { internal int width, height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { internal byte operation, flags, alpha, format; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref NativePoint destination, ref NativeSize size, IntPtr source, ref NativePoint origin, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr item);
        internal MarkerWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // Layered + transparent passes input to windows below; never activate or enter Alt-Tab.
                parameters.ExStyle |= 0x00080000 | 0x08000000 | 0x00000080;
                if (!editable) parameters.ExStyle |= 0x00000020;
                return parameters;
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RegisterMarker(Handle); }
        protected override void OnHandleDestroyed(EventArgs e) { Native.UnregisterMarker(Handle); base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(editable ? 1 : -1); return; }
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
        internal void UpdateMarker(Point point, int index, int mouseButton, int size)
        {
            UpdateMarker(point, index, mouseButton, size, false, null);
        }
        internal void UpdateMarker(Point point, int index, int mouseButton, int size, bool edit, Action<int, Point> onMoved)
        {
            // Cancel an unfinished drag if the route or run state changes before mouse-up.
            if (dragging) { dragging = false; Capture = false; }
            if (editable != edit)
            {
                dragging = false;
                Capture = false;
                editable = edit;
                Cursor = edit ? Cursors.SizeAll : Cursors.Default;
                if (IsHandleCreated) UpdateStyles();
            }
            moved = onMoved;
            number = index;
            button = mouseButton;
            Text = "点击标记 " + index + " · " + PointBadge.ButtonName(mouseButton);
            SetBounds(point.X - size / 2, point.Y - size / 2, size, size);
            RenderLayered();
            if (!Visible) Show();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!editable || e.Button != MouseButtons.Left) return;
            dragCursor = Cursor.Position;
            dragOrigin = Location;
            dragging = true;
            Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point cursor = Cursor.Position;
            Location = new Point(dragOrigin.X + cursor.X - dragCursor.X, dragOrigin.Y + cursor.Y - dragCursor.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;
            Capture = false;
            if (moved != null) moved(number, new Point(Left + Width / 2, Top + Height / 2));
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture) { dragging = false; Location = dragOrigin; }
        }
        private void RenderLayered()
        {
            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    PointBadge.Draw(graphics, new Rectangle(0, 0, Width, Height), number, button, highlighted);
                }
                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr memory = CreateCompatibleDC(screen);
                IntPtr image = IntPtr.Zero, previous = IntPtr.Zero;
                try
                {
                    image = bitmap.GetHbitmap(Color.FromArgb(0));
                    previous = SelectObject(memory, image);
                    NativePoint destination = new NativePoint { x = Left, y = Top };
                    NativePoint origin = new NativePoint();
                    NativeSize size = new NativeSize { width = Width, height = Height };
                    Blend blend = new Blend { alpha = 255, format = 1 };
                    if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    if (previous != IntPtr.Zero) SelectObject(memory, previous);
                    if (image != IntPtr.Zero) DeleteObject(image);
                    if (memory != IntPtr.Zero) DeleteDC(memory);
                    if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
    }

    internal sealed class MarkerLayer : IDisposable
    {
        private readonly List<MarkerWindow> markers = new List<MarkerWindow>();
        private int used;
        internal void BeginUpdate() { used = 0; }
        internal void Add(Point point, int number, int button, int size)
        {
            Add(point, number, button, size, false, null);
        }
        internal void Add(Point point, int number, int button, int size, bool editable, Action<int, Point> moved)
        {
            if (used == markers.Count) markers.Add(new MarkerWindow());
            markers[used++].UpdateMarker(point, number, button, size, editable, moved);
        }
        internal void EndUpdate()
        {
            while (markers.Count > used)
            {
                int last = markers.Count - 1;
                markers[last].Dispose();
                markers.RemoveAt(last);
            }
        }
        internal void Highlight(int number) { foreach (MarkerWindow marker in markers) marker.Highlight(number > 0 && marker.Number == number); }
        public void Dispose() { used = 0; EndUpdate(); }
    }
}
