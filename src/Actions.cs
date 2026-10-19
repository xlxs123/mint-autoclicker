using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MintClicker
{
    public enum ActionKind { Click, Wait, Key, Move, Scroll, Pixel, Drag, FindImage }

    // Portable data only. Runtime behavior lives in the ActionStep subclasses below.
    public sealed class MacroEntry
    {
        public ActionKind Kind;
        public bool Enabled = true;
        public int X, Y, Button, DelayBefore, DelayAfter;
        public bool DoubleClick;
        public int Count = 1, RepeatInterval = 100, Duration = 1000;
        public int KeyCode = (int)Keys.Enter;
        public bool Control, Alt, Shift;
        public int Wheel = -1;
        // Pixel step: wait until the colour at X/Y is within Tolerance of PixelColor.
        public int PixelColor = unchecked((int)0xFFFF0000);
        public int PixelTolerance = 16;
        // Drag step: press at X/Y, move to EndX/EndY over Duration milliseconds, release.
        public int EndX, EndY;
        // Find-image step: base64 PNG of the picture to look for; reuses PixelTolerance
        // as the per-channel match tolerance and Duration as the search timeout.
        public string Template = "";
        public int TemplateWidth, TemplateHeight;
    }

    internal abstract class ActionStep
    {
        internal int Before, After, Number;
        internal abstract bool Execute(ClickEngine engine, ClickOptions options);
    }

    internal sealed class MouseClickStep : ActionStep
    {
        internal Point? Target;
        internal int Button, Count = 1, Interval = 100;
        internal bool Twice;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            for (int i = 0; i < Count; i++)
            {
                engine.SetRepeat(i + 1, Count);
                if (!engine.Dispatch("点击", delegate
                {
                    // Jitter only moves explicit targets; follow-mouse keeps the user's cursor.
                    Point? target = Target.HasValue ? engine.JitteredPoint(engine.ResolvePoint(Target.Value)) : (Point?)null;
                    ActionCompiler.CheckTarget(target ?? engine.Input.CursorPoint(), options, engine.Input);
                    engine.Input.Click(Button, Twice, target, options.Desktop);
                })) return false;
                if (i + 1 < Count && !engine.Wait(Interval, "点内间隔")) return false;
            }
            return true;
        }
    }
    internal sealed class WaitStep : ActionStep
    {
        internal int Duration;
        internal override bool Execute(ClickEngine engine, ClickOptions options) { return engine.Wait(Duration, "等待动作"); }
    }
    internal sealed class KeyPressStep : ActionStep
    {
        internal int Code;
        internal bool Control, Alt, Shift;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("键盘", delegate
            {
                if (engine.Input.ForegroundWindow() == options.MainWindow) throw new InvalidOperationException("目标为连点器自身，已停止键盘动作");
                engine.Input.KeyPress(Code, Control, Alt, Shift);
            });
        }
    }
    internal sealed class MouseMoveStep : ActionStep
    {
        internal Point Target;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("移动鼠标", delegate
            {
                Point screen = engine.ResolvePoint(Target);
                ActionCompiler.CheckTarget(screen, options, engine.Input);
                engine.Input.Move(screen, options.Desktop);
            });
        }
    }
    internal sealed class ScrollStep : ActionStep
    {
        internal Point Target;
        internal int Notches;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("滚轮", delegate
            {
                Point screen = engine.ResolvePoint(Target);
                ActionCompiler.CheckTarget(screen, options, engine.Input);
                engine.Input.Scroll(screen, Notches, options.Desktop);
            });
        }
    }
    // Presses at Start, moves to End over Duration, then releases.
    internal sealed class DragStep : ActionStep
    {
        internal Point Start, End;
        internal int Button, Duration;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("拖动", delegate
            {
                Point start = engine.ResolvePoint(Start), end = engine.ResolvePoint(End);
                ActionCompiler.CheckTarget(start, options, engine.Input);
                ActionCompiler.CheckTarget(end, options, engine.Input);
                engine.Input.Drag(Button, start, end, Duration, options.Desktop);
            });
        }
    }
    // Waits for the screen pixel at Target to match Expected (within Tolerance).
    internal sealed class PixelStep : ActionStep
    {
        internal Point Target;
        internal Color Expected;
        internal int Tolerance, Timeout;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.WaitUntil(delegate
            {
                Color actual = engine.Input.Pixel(engine.ResolvePoint(Target));
                return Math.Abs(actual.R - Expected.R) <= Tolerance &&
                       Math.Abs(actual.G - Expected.G) <= Tolerance &&
                       Math.Abs(actual.B - Expected.B) <= Tolerance;
            }, Timeout, "等待颜色");
        }
    }

    // Waits for a template image to appear in the search area, then clicks its centre.
    internal sealed class FindImageStep : ActionStep
    {
        internal int[] Pixels;
        internal int Width, Height;
        internal int Tolerance, Timeout, Button;
        internal bool Twice;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            Rectangle area = engine.SearchArea();
            if (area.Width < Width || area.Height < Height)
                throw new InvalidOperationException("搜索区域比找图模板还小，已停止");
            long lastScan = 0;
            Point? center = null;
            bool found = engine.WaitUntil(delegate
            {
                if (center.HasValue) return true;
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                if (now - lastScan < ScanInterval) return false;
                lastScan = now;
                Point match;
                int[] screen = engine.Input.CaptureScreen(area);
                if (!TemplateMatcher.Find(screen, area.Width, area.Height, Pixels, Width, Height, Tolerance, out match)) return false;
                center = new Point(area.X + match.X + Width / 2, area.Y + match.Y + Height / 2);
                return true;
            }, Timeout, "等待图片");
            if (!found || !center.HasValue) return false;
            Point clickPoint = center.Value;
            return engine.Dispatch("点击图片", delegate
            {
                Point point = engine.JitteredPoint(clickPoint);
                ActionCompiler.CheckTarget(point, options, engine.Input);
                engine.Input.Click(Button, Twice, point, options.Desktop);
            });
        }
        private const int ScanInterval = 250;
    }

    internal static class ActionCompiler
    {
        internal static string Name(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Click: return "点击";
                case ActionKind.Wait: return "等待";
                case ActionKind.Key: return "按键";
                case ActionKind.Move: return "移动";
                case ActionKind.Pixel: return "等颜色";
                case ActionKind.Drag: return "拖动";
                case ActionKind.FindImage: return "找图";
                default: return "滚轮";
            }
        }
        internal static string KeyName(MacroEntry entry)
        {
            return (entry.Control ? "Ctrl+" : "") + (entry.Alt ? "Alt+" : "") + (entry.Shift ? "Shift+" : "") + ((Keys)entry.KeyCode).ToString();
        }
        internal static string Description(MacroEntry entry)
        {
            switch (entry.Kind)
            {
                case ActionKind.Wait: return "等待 " + entry.Duration + " ms";
                case ActionKind.Key: return KeyName(entry);
                case ActionKind.Move: return "X " + entry.X + " / Y " + entry.Y;
                case ActionKind.Scroll: return (entry.Wheel > 0 ? "向上 " : "向下 ") + Math.Abs(entry.Wheel) + " 格 · " + entry.X + ", " + entry.Y;
                case ActionKind.Pixel: return "等 (" + entry.X + ", " + entry.Y + ") 变为 " + ColorTranslator.ToHtml(Color.FromArgb(entry.PixelColor)) +
                    " ±" + entry.PixelTolerance + " · 超时 " + entry.Duration + " ms";
                case ActionKind.Drag: return "拖动 " + PointBadge.ButtonName(entry.Button) + " (" + entry.X + ", " + entry.Y + ") → (" +
                    entry.EndX + ", " + entry.EndY + ") · 按住 " + entry.Duration + " ms";
                case ActionKind.FindImage: return "找图并点击 · 模板 " + entry.TemplateWidth + "×" + entry.TemplateHeight +
                    " · 容差 " + entry.PixelTolerance + " · 超时 " + (entry.Duration == 0 ? "不限" : entry.Duration + " ms");
                default: return PointBadge.ButtonName(entry.Button) + (entry.DoubleClick ? "双击" : "单击") + " × " + entry.Count + " · " + entry.X + ", " + entry.Y;
            }
        }
        internal static void Validate(MacroEntry entry)
        {
            if (entry == null || !Enum.IsDefined(typeof(ActionKind), entry.Kind)) throw new InvalidDataException("宏步骤类型无效。");
            if (entry.X < -1000000 || entry.X > 1000000 || entry.Y < -1000000 || entry.Y > 1000000 ||
                entry.EndX < -1000000 || entry.EndX > 1000000 || entry.EndY < -1000000 || entry.EndY > 1000000 ||
                entry.Button < 0 || entry.Button > 2 || entry.Count < 1 || entry.Count > 10000 ||
                entry.RepeatInterval < 0 || entry.RepeatInterval > 3600000 || entry.Duration < 0 || entry.Duration > 3600000 ||
                entry.DelayBefore < 0 || entry.DelayBefore > 3600000 || entry.DelayAfter < 0 || entry.DelayAfter > 3600000 ||
                entry.Wheel < -100 || entry.Wheel > 100 || (entry.Kind == ActionKind.Scroll && entry.Wheel == 0) ||
                entry.PixelTolerance < 0 || entry.PixelTolerance > 255)
                throw new InvalidDataException("宏步骤的坐标、次数或等待时间超出范围。");
            if (entry.Kind == ActionKind.Key)
            {
                int key = entry.KeyCode;
                if (key < 8 || key > 254 || key == 16 || key == 17 || key == 18 || key == 91 || key == 92 || key == 93 ||
                    (key >= 160 && key <= 165) || (key >= 117 && key <= 120) || key == 123 ||
                    (entry.Control && entry.Alt && key == (int)Keys.A) || !Enum.IsDefined(typeof(Keys), (Keys)key))
                    throw new InvalidDataException("请选择有效按键；F6/F7/F8/F9/F12、Ctrl+Alt+A 留给连点器，不可用作宏按键。");
            }
            if (entry.Kind == ActionKind.FindImage)
            {
                int[] pixels;
                int width, height;
                if (!TemplateImage.TryDecode(entry.Template, out pixels, out width, out height))
                    throw new InvalidDataException("找图步骤需要先截取模板（8～200 像素的 PNG 图片）。");
                // Normalise the stored size with what the image really contains.
                entry.TemplateWidth = width;
                entry.TemplateHeight = height;
            }
        }
        internal static void CheckTarget(Point point, ClickOptions options, IInput input)
        {
            if (!input.IsOnScreen(point) || (point.X >= 0 && point.X < 5 && point.Y >= 0 && point.Y < 5))
                throw new InvalidOperationException("目标离开屏幕或进入紧急停止区，已停止");
            if (input.IsOwnWindow(point, options.MainWindow)) throw new InvalidOperationException("目标位于连点器窗口，已停止");
        }
        internal static ActionStep[] Build(ClickOptions options)
        {
            List<ActionStep> steps = new List<ActionStep>();
            if (options.Actions != null)
            {
                for (int i = 0; i < options.Actions.Length; i++)
                {
                    MacroEntry data = options.Actions[i];
                    Validate(data);
                    ActionStep step;
                    Point target = new Point(data.X, data.Y);
                    switch (data.Kind)
                    {
                        case ActionKind.Wait: step = new WaitStep { Duration = data.Duration }; break;
                        case ActionKind.Key: step = new KeyPressStep { Code = data.KeyCode, Control = data.Control, Alt = data.Alt, Shift = data.Shift }; break;
                        case ActionKind.Move: step = new MouseMoveStep { Target = target }; break;
                        case ActionKind.Scroll: step = new ScrollStep { Target = target, Notches = data.Wheel }; break;
                        case ActionKind.Pixel: step = new PixelStep { Target = target, Expected = Color.FromArgb(data.PixelColor), Tolerance = data.PixelTolerance, Timeout = data.Duration }; break;
                        case ActionKind.Drag: step = new DragStep { Start = target, End = new Point(data.EndX, data.EndY), Button = data.Button, Duration = data.Duration }; break;
                        case ActionKind.FindImage:
                        {
                            int[] pixels;
                            int width, height;
                            if (!TemplateImage.TryDecode(data.Template, out pixels, out width, out height))
                                throw new InvalidDataException("找图模板图片无效。");
                            step = new FindImageStep
                            {
                                Pixels = pixels, Width = width, Height = height, Tolerance = data.PixelTolerance,
                                Timeout = data.Duration, Button = data.Button, Twice = data.DoubleClick
                            };
                            break;
                        }
                        default: step = new MouseClickStep { Target = target, Button = data.Button, Twice = data.DoubleClick, Count = data.Count, Interval = data.RepeatInterval }; break;
                    }
                    step.Before = data.DelayBefore;
                    step.After = data.DelayAfter;
                    step.Number = options.SourceNumbers == null ? i + 1 : options.SourceNumbers[i];
                    steps.Add(step);
                }
            }
            else
            {
                int count = options.Positions == null ? 1 : options.Positions.Length;
                for (int i = 0; i < count; i++)
                {
                    // -1 on the point means "use the global single/double click choice".
                    int perPoint = options.PositionDoubleClicks == null ? -1 : options.PositionDoubleClicks[i];
                    steps.Add(new MouseClickStep
                    {
                        Target = options.Positions == null ? options.FixedPoint : options.Positions[i],
                        Button = options.PositionButtons == null ? options.Button : options.PositionButtons[i],
                        Twice = perPoint < 0 ? options.DoubleClick : perPoint == 1,
                        Count = options.RepeatCounts == null ? 1 : options.RepeatCounts[i], Interval = options.RepeatIntervals == null ? 100 : options.RepeatIntervals[i],
                        Before = options.DelaysBefore == null ? 0 : options.DelaysBefore[i], After = options.DelaysAfter == null ? options.Interval : options.DelaysAfter[i],
                        Number = options.SourceNumbers == null ? i + 1 : options.SourceNumbers[i]
                    });
                }
            }
            if (steps.Count == 0) throw new ArgumentException("至少需要一个已勾选步骤。");
            return steps.ToArray();
        }
    }
}
