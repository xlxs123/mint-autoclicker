using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MintClicker
{
    public enum ActionKind { Click, Wait, Key, Move, Scroll }

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
                    ActionCompiler.CheckTarget(Target ?? Native.CursorPoint(), options);
                    Native.Click(Button, Twice, Target, options.Desktop);
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
                if (Native.GetForegroundWindow() == options.MainWindow) throw new InvalidOperationException("目标为连点器自身，已停止键盘动作");
                Native.KeyPress(Code, Control, Alt, Shift);
            });
        }
    }
    internal sealed class MouseMoveStep : ActionStep
    {
        internal Point Target;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("移动鼠标", delegate { ActionCompiler.CheckTarget(Target, options); Native.Move(Target, options.Desktop); });
        }
    }
    internal sealed class ScrollStep : ActionStep
    {
        internal Point Target;
        internal int Notches;
        internal override bool Execute(ClickEngine engine, ClickOptions options)
        {
            return engine.Dispatch("滚轮", delegate { ActionCompiler.CheckTarget(Target, options); Native.Scroll(Target, Notches, options.Desktop); });
        }
    }

    internal static class ActionCompiler
    {
        internal static string Name(ActionKind kind)
        {
            switch (kind) { case ActionKind.Click: return "点击"; case ActionKind.Wait: return "等待"; case ActionKind.Key: return "按键"; case ActionKind.Move: return "移动"; default: return "滚轮"; }
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
                default: return PointBadge.ButtonName(entry.Button) + (entry.DoubleClick ? "双击" : "单击") + " × " + entry.Count + " · " + entry.X + ", " + entry.Y;
            }
        }
        internal static void Validate(MacroEntry entry)
        {
            if (entry == null || !Enum.IsDefined(typeof(ActionKind), entry.Kind)) throw new InvalidDataException("宏步骤类型无效。");
            if (entry.X < -1000000 || entry.X > 1000000 || entry.Y < -1000000 || entry.Y > 1000000 ||
                entry.Button < 0 || entry.Button > 2 || entry.Count < 1 || entry.Count > 10000 ||
                entry.RepeatInterval < 0 || entry.RepeatInterval > 3600000 || entry.Duration < 0 || entry.Duration > 3600000 ||
                entry.DelayBefore < 0 || entry.DelayBefore > 3600000 || entry.DelayAfter < 0 || entry.DelayAfter > 3600000 ||
                entry.Wheel < -100 || entry.Wheel > 100 || (entry.Kind == ActionKind.Scroll && entry.Wheel == 0))
                throw new InvalidDataException("宏步骤的坐标、次数或等待时间超出范围。");
            if (entry.Kind == ActionKind.Key)
            {
                int key = entry.KeyCode;
                if (key < 8 || key > 254 || key == 16 || key == 17 || key == 18 || key == 91 || key == 92 || key == 93 ||
                    (key >= 160 && key <= 165) || (key >= 117 && key <= 120) || key == 123 ||
                    (entry.Control && entry.Alt && key == (int)Keys.A) || !Enum.IsDefined(typeof(Keys), (Keys)key))
                    throw new InvalidDataException("请选择有效按键；F6/F7/F8/F9/F12、Ctrl+Alt+A 留给连点器，不可用作宏按键。");
            }
        }
        internal static void CheckTarget(Point point, ClickOptions options)
        {
            bool onScreen = false;
            foreach (Screen screen in Screen.AllScreens) if (screen.Bounds.Contains(point)) { onScreen = true; break; }
            if (!onScreen || (point.X >= 0 && point.X < 5 && point.Y >= 0 && point.Y < 5))
                throw new InvalidOperationException("目标离开屏幕或进入紧急停止区，已停止");
            if (Native.IsOwnWindow(point, options.MainWindow)) throw new InvalidOperationException("目标位于连点器窗口，已停止");
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
                for (int i = 0; i < count; i++) steps.Add(new MouseClickStep
                {
                    Target = options.Positions == null ? options.FixedPoint : options.Positions[i],
                    Button = options.PositionButtons == null ? options.Button : options.PositionButtons[i], Twice = options.DoubleClick,
                    Count = options.RepeatCounts == null ? 1 : options.RepeatCounts[i], Interval = options.RepeatIntervals == null ? 100 : options.RepeatIntervals[i],
                    Before = options.DelaysBefore == null ? 0 : options.DelaysBefore[i], After = options.DelaysAfter == null ? options.Interval : options.DelaysAfter[i],
                    Number = options.SourceNumbers == null ? i + 1 : options.SourceNumbers[i]
                });
            }
            if (steps.Count == 0) throw new ArgumentException("至少需要一个已勾选步骤。");
            return steps.ToArray();
        }
    }
}
