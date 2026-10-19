using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace MintClicker
{
    internal sealed class ClickOptions
    {
        internal int Interval, Button;
        internal bool DoubleClick;
        internal long Limit;
        internal Point? FixedPoint;
        internal Point[] Positions;
        internal int[] PositionButtons, DelaysBefore, DelaysAfter, RepeatCounts, RepeatIntervals, SourceNumbers;
        internal int[] PositionDoubleClicks;
        internal bool HighPrecision = true;
        internal MacroEntry[] Actions;
        internal Rectangle Desktop;
        internal IntPtr MainWindow, TargetWindow;
        internal uint TargetProcessId;
        internal int LoopStart = -1, LoopEnd = -1, LoopCount = 1;
        // Randomization and self-stop, both 0 when disabled.
        internal int JitterInterval, JitterPosition;
        internal long TimeLimitMs;
        // When true, stored points are client-area offsets of TargetWindow.
        internal bool RelativeToTarget;
    }

    internal sealed class ClickEngine : IDisposable
    {
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly object sync = new object();
        private readonly Stopwatch watch = new Stopwatch();
        private readonly Random random = new Random();
        private Thread worker;
        private long rounds, actions;
        private bool precisionRaised;
        private volatile bool stuck;
        private int currentPosition, sourceNumber, remaining, repetition, repetitions, stepCount;
        private volatile bool running, paused, autoPaused;
        private volatile string reason = "已就绪", phase = "就绪";
        private ClickOptions active;
        internal long Rounds { get { return Interlocked.Read(ref rounds); } }
        // Actions actually handed to Windows, for the measured-rate readout.
        internal long Actions { get { return Interlocked.Read(ref actions); } }
        internal bool Stuck { get { return stuck; } }
        internal int CurrentPosition { get { return Volatile.Read(ref currentPosition); } }
        internal int SourceNumber { get { return Volatile.Read(ref sourceNumber); } }
        internal int StepCount { get { return stepCount; } }
        internal int Remaining { get { return Volatile.Read(ref remaining); } }
        internal int Repetition { get { return Volatile.Read(ref repetition); } }
        internal int Repetitions { get { return Volatile.Read(ref repetitions); } }
        // Deliberately lock-free: the UI reads this every tick, and the worker holds
        // "sync" while a native input call is in flight. A torn read is harmless.
        internal TimeSpan Elapsed { get { return watch.Elapsed; } }
        internal bool Running { get { return running; } }
        internal bool Paused { get { return paused; } }
        // True only when the target-window guard paused the run, not the user.
        internal bool AutoPaused { get { return autoPaused; } }
        // Swapped by the test suite; production always uses the real input stack.
        internal IInput Input { get; set; }

        internal ClickEngine() { Input = NativeInput.Instance; }
        internal string Reason { get { return reason; } }
        internal string Phase { get { return phase; } }

        internal void Start(ClickOptions options)
        {
            if (options.Interval < 10 || options.Interval > 3600000 || options.Limit < 0 || options.Button < 0 || options.Button > 2)
                throw new ArgumentOutOfRangeException("options");
            int count = options.Actions != null ? options.Actions.Length : options.Positions == null ? 1 : options.Positions.Length;
            if (count == 0 || count > 1000) throw new ArgumentException("步骤数量必须为 1～1000。");
            ValidateArray(options.PositionButtons, count, 0, 2);
            ValidateArray(options.DelaysBefore, count, 0, 3600000);
            ValidateArray(options.DelaysAfter, count, 0, 3600000);
            ValidateArray(options.RepeatCounts, count, 1, 10000);
            ValidateArray(options.RepeatIntervals, count, 0, 3600000);
            ValidateArray(options.SourceNumbers, count, 1, 1000);
            ValidateArray(options.PositionDoubleClicks, count, -1, 1);
            if (options.LoopCount < 1 || options.LoopCount > 10000 ||
                (options.LoopCount > 1 && (options.LoopStart < 0 || options.LoopEnd < options.LoopStart || options.LoopEnd >= count)))
                throw new ArgumentException("局部循环范围无效。");
            // Compilation copies every action parameter into private runtime steps.
            ActionStep[] steps = ActionCompiler.Build(options);
            ClickOptions snapshot = new ClickOptions { Interval = options.Interval, Button = options.Button, DoubleClick = options.DoubleClick,
                Limit = options.Limit, Desktop = options.Desktop, MainWindow = options.MainWindow,
                TargetWindow = options.TargetWindow, TargetProcessId = options.TargetProcessId,
                LoopStart = options.LoopStart, LoopEnd = options.LoopEnd, LoopCount = options.LoopCount,
                JitterInterval = options.JitterInterval, JitterPosition = options.JitterPosition, TimeLimitMs = options.TimeLimitMs,
                RelativeToTarget = options.RelativeToTarget };
            if (!TryReap()) throw new InvalidOperationException("上一次运行还没有退出，请稍后再试。");
            Stop("已停止");
            RaisePrecision(options.HighPrecision);
            lock (sync)
            {
                active = snapshot;
                stop.Reset();
                Interlocked.Exchange(ref rounds, 0);
                Interlocked.Exchange(ref actions, 0);
                currentPosition = sourceNumber = remaining = repetition = repetitions = 0;
                stepCount = steps.Length;
                paused = false;
                autoPaused = false;
                reason = "正在运行";
                phase = "准备";
                running = true;
                watch.Restart();
            }
            worker = new Thread(delegate() { Run(snapshot, steps); }) { IsBackground = true, Name = "AutoClicker" };
            worker.Start();
        }

        private static void ValidateArray(int[] values, int count, int min, int max)
        {
            if (values == null) return;
            if (values.Length != count) throw new ArgumentException("步骤与参数数量不匹配。");
            foreach (int value in values) if (value < min || value > max) throw new ArgumentOutOfRangeException("options");
        }

        internal void TogglePause()
        {
            lock (sync)
            {
                if (!running) return;
                if (paused)
                {
                    if (!TargetReady()) { reason = "请先切回绑定的目标窗口，再按 F9 恢复"; return; }
                    paused = false;
                    autoPaused = false;
                    reason = "正在运行";
                    watch.Start();
                }
                else { paused = true; autoPaused = false; reason = "已暂停 · F9 继续"; watch.Stop(); }
            }
        }

        private bool TargetReady()
        {
            return active == null || active.TargetWindow == IntPtr.Zero ||
                Input.IsTargetForeground(active.TargetWindow, active.TargetProcessId);
        }

        private bool Gate()
        {
            while (!stop.WaitOne(0))
            {
                if (Emergency()) return false;
                ClickOptions current = active;
                if (current != null && current.TimeLimitMs > 0 && watch.ElapsedMilliseconds >= current.TimeLimitMs)
                { reason = "已达到运行时长，已停止"; return false; }
                lock (sync)
                {
                    if (!TargetReady() && !paused)
                    {
                        paused = true;
                        autoPaused = true;
                        reason = "目标窗口变化，已暂停 · 切回目标后 F9 恢复";
                        watch.Stop();
                        Log.Info("目标窗口变化，已自动暂停");
                    }
                    if (!paused) return true;
                }
                if (stop.WaitOne(10)) return false;
            }
            return false;
        }

        // Converts a stored point to screen coordinates. Re-read every time so the
        // points keep working when the target window moves during a run.
        internal Point ResolvePoint(Point point)
        {
            ClickOptions current = active;
            if (current == null || !current.RelativeToTarget || current.TargetWindow == IntPtr.Zero) return point;
            Point origin;
            if (!Input.ClientOrigin(current.TargetWindow, out origin))
                throw new InvalidOperationException("目标窗口已关闭，已停止");
            return new Point(origin.X + point.X, origin.Y + point.Y);
        }

        // Area searched by the find-image step: the bound target's client area when it
        // is available, otherwise the whole virtual desktop.
        internal Rectangle SearchArea()
        {
            ClickOptions current = active;
            if (current == null) return Rectangle.Empty;
            if (current.TargetWindow != IntPtr.Zero)
            {
                Rectangle area;
                if (Input.ClientArea(current.TargetWindow, out area))
                {
                    Rectangle overlap = Rectangle.Intersect(area, current.Desktop);
                    if (overlap.Width > 0 && overlap.Height > 0) return overlap;
                }
            }
            return current.Desktop;
        }

        // A pause request and one atomic input batch share a lock: no new batch starts after Pause returns.
        internal bool Dispatch(string description, Action action)
        {
            while (Gate())
            {
                lock (sync)
                {
                    if (paused || stop.WaitOne(0)) continue;
                    if (!TargetReady()) continue;
                    if (Emergency()) return false;
                    phase = description;
                    Volatile.Write(ref remaining, 0);
                    action();
                    Interlocked.Increment(ref actions);
                    return true;
                }
            }
            return false;
        }

        internal void SetRepeat(int current, int total)
        {
            Volatile.Write(ref repetition, current);
            Volatile.Write(ref repetitions, total);
        }

        internal bool Wait(int milliseconds, string description)
        {
            phase = description;
            int wait = Jitter(milliseconds);
            long until;
            lock (sync) until = watch.ElapsedMilliseconds + wait;
            Volatile.Write(ref remaining, wait);
            while (Gate())
            {
                long left;
                lock (sync) left = until - watch.ElapsedMilliseconds;
                Volatile.Write(ref remaining, (int)Math.Max(0, left));
                if (left <= 0) return true;
                if (stop.WaitOne((int)Math.Min(10, left))) return false;
            }
            return false;
        }

        // ± JitterInterval around the requested delay, never negative.
        private int Jitter(int milliseconds)
        {
            ClickOptions current = active;
            if (milliseconds <= 0 || current == null || current.JitterInterval <= 0) return milliseconds;
            return Math.Max(1, milliseconds + random.Next(-current.JitterInterval, current.JitterInterval + 1));
        }

        // ± JitterPosition around an explicit target, clamped to the virtual desktop.
        internal Point JitteredPoint(Point point)
        {
            ClickOptions current = active;
            if (current == null || current.JitterPosition <= 0) return point;
            Rectangle bounds = current.Desktop;
            int range = current.JitterPosition;
            int x = point.X + random.Next(-range, range + 1);
            int y = point.Y + random.Next(-range, range + 1);
            x = Math.Max(bounds.Left, Math.Min(bounds.Right - 1, x));
            y = Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, y));
            return new Point(x, y);
        }

        // Polls a condition (screen colour) while honouring pause, stop and the run limit.
        internal bool WaitUntil(Func<bool> condition, int timeoutMilliseconds, string description)
        {
            phase = description;
            long deadline = timeoutMilliseconds > 0 ? watch.ElapsedMilliseconds + timeoutMilliseconds : 0;
            while (Gate())
            {
                if (condition()) return true;
                long elapsed = watch.ElapsedMilliseconds;
                if (deadline > 0 && elapsed >= deadline) { reason = description + "超时"; return false; }
                long left = deadline > 0 ? deadline - elapsed : 1000;
                int slice = (int)Math.Max(1, Math.Min(50, left));
                Volatile.Write(ref remaining, slice);
                if (stop.WaitOne(slice)) return false;
            }
            return false;
        }

        private void Run(ClickOptions options, ActionStep[] steps)
        {
            try
            {
                int index = 0, innerLoop = 1;
                while (Gate())
                {
                    ActionStep step = steps[index];
                    Volatile.Write(ref currentPosition, index + 1);
                    Volatile.Write(ref sourceNumber, step.Number);
                    SetRepeat(0, 0);
                    if (!Wait(step.Before, "执行前等待") || !step.Execute(this, options)) return;
                    if (options.LoopCount > 1 && index == options.LoopEnd && innerLoop < options.LoopCount)
                    { innerLoop++; index = options.LoopStart; }
                    else index++;
                    long done = Rounds;
                    if (index == steps.Length)
                    { index = 0; innerLoop = 1; done = Interlocked.Increment(ref rounds); }
                    if (options.Limit > 0 && done >= options.Limit)
                    { reason = "已完成设定轮数"; return; }
                    if (!Wait(step.After, "执行后等待")) return;
                }
            }
            catch (Exception ex) { reason = ex.Message; Log.Error("运行中出错：" + reason, ex); }
            finally
            {
                lock (sync)
                {
                    watch.Stop(); running = false; paused = false; autoPaused = false; phase = "结束"; remaining = 0;
                    RaisePrecision(false);
                }
            }
        }

        private bool Emergency()
        {
            if ((Input.KeyState(0x77) & 0x8000) != 0 || (Input.KeyState(0x7B) & 0x8000) != 0)
            { reason = "已通过 F8 / F12 停止"; return true; }
            Point point = Input.CursorPoint();
            if (point.X >= 0 && point.X < 5 && point.Y >= 0 && point.Y < 5)
            { reason = "已触发左上角紧急停止"; return true; }
            return false;
        }

        // Only raise the system timer while a run is active: 1 ms sleeps cost power.
        private void RaisePrecision(bool raise)
        {
            lock (sync)
            {
                if (raise == precisionRaised) return;
                precisionRaised = raise;
                if (raise) Input.BeginPrecision(); else Input.EndPrecision();
            }
        }

        // A worker blocked inside SendInput cannot be stopped; keep its reference so a
        // new run cannot reset the stop event underneath it.
        private bool TryReap()
        {
            Thread current = worker;
            if (current == null) return true;
            if (current.IsAlive) return false;
            worker = null;
            stuck = false;
            return true;
        }

        internal void Stop(string message)
        {
            stop.Set();
            Thread current = worker;
            if (current != null && current.IsAlive && !current.Join(3000))
            {
                stuck = true;
                Log.Error("停止等待超时：输入调用尚未返回", null);
                lock (sync)
                {
                    running = false; paused = false; autoPaused = false; watch.Stop(); remaining = 0;
                    if (message != null) reason = message + "（输入未返回）";
                    RaisePrecision(false);
                }
                return;
            }
            worker = null;
            stuck = false;
            lock (sync)
            {
                if (message != null) reason = message;
                running = false; paused = false; autoPaused = false; watch.Stop(); remaining = 0;
                RaisePrecision(false);
            }
        }
        public void Dispose() { Stop("已停止"); stop.Dispose(); }
    }
}
