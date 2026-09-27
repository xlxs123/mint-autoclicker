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
        internal MacroEntry[] Actions;
        internal Rectangle Desktop;
        internal IntPtr MainWindow, TargetWindow;
        internal uint TargetProcessId;
        internal int LoopStart = -1, LoopEnd = -1, LoopCount = 1;
    }

    internal sealed class ClickEngine : IDisposable
    {
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly object sync = new object();
        private readonly Stopwatch watch = new Stopwatch();
        private Thread worker;
        private long rounds;
        private int currentPosition, sourceNumber, remaining, repetition, repetitions, stepCount;
        private volatile bool running, paused;
        private volatile string reason = "已就绪", phase = "就绪";
        private ClickOptions active;
        internal long Rounds { get { return Interlocked.Read(ref rounds); } }
        internal int CurrentPosition { get { return Volatile.Read(ref currentPosition); } }
        internal int SourceNumber { get { return Volatile.Read(ref sourceNumber); } }
        internal int StepCount { get { return stepCount; } }
        internal int Remaining { get { return Volatile.Read(ref remaining); } }
        internal int Repetition { get { return Volatile.Read(ref repetition); } }
        internal int Repetitions { get { return Volatile.Read(ref repetitions); } }
        internal TimeSpan Elapsed { get { lock (sync) return watch.Elapsed; } }
        internal bool Running { get { return running; } }
        internal bool Paused { get { return paused; } }
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
            if (options.LoopCount < 1 || options.LoopCount > 10000 ||
                (options.LoopCount > 1 && (options.LoopStart < 0 || options.LoopEnd < options.LoopStart || options.LoopEnd >= count)))
                throw new ArgumentException("局部循环范围无效。");
            // Compilation copies every action parameter into private runtime steps.
            ActionStep[] steps = ActionCompiler.Build(options);
            ClickOptions snapshot = new ClickOptions { Interval = options.Interval, Button = options.Button, DoubleClick = options.DoubleClick,
                Limit = options.Limit, Desktop = options.Desktop, MainWindow = options.MainWindow,
                TargetWindow = options.TargetWindow, TargetProcessId = options.TargetProcessId,
                LoopStart = options.LoopStart, LoopEnd = options.LoopEnd, LoopCount = options.LoopCount };
            Stop("已停止");
            lock (sync)
            {
                active = snapshot;
                stop.Reset();
                Interlocked.Exchange(ref rounds, 0);
                currentPosition = sourceNumber = remaining = repetition = repetitions = 0;
                stepCount = steps.Length;
                paused = false;
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
                    reason = "正在运行";
                    watch.Start();
                }
                else { paused = true; reason = "已暂停 · F9 继续"; watch.Stop(); }
            }
        }

        private bool TargetReady()
        {
            return active == null || active.TargetWindow == IntPtr.Zero ||
                Native.IsTargetForeground(active.TargetWindow, active.TargetProcessId);
        }

        private bool Gate()
        {
            while (!stop.WaitOne(0))
            {
                if (Emergency()) return false;
                lock (sync)
                {
                    if (!TargetReady() && !paused)
                    {
                        paused = true;
                        reason = "目标窗口变化，已暂停 · 切回目标后 F9 恢复";
                        watch.Stop();
                    }
                    if (!paused) return true;
                }
                if (stop.WaitOne(10)) return false;
            }
            return false;
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
            long until;
            lock (sync) until = watch.ElapsedMilliseconds + milliseconds;
            Volatile.Write(ref remaining, milliseconds);
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
            catch (Exception ex) { reason = ex.Message; }
            finally
            {
                lock (sync) { watch.Stop(); running = false; paused = false; phase = "结束"; remaining = 0; }
            }
        }

        private bool Emergency()
        {
            if ((Native.GetAsyncKeyState(0x77) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x7B) & 0x8000) != 0)
            { reason = "已通过 F8 / F12 停止"; return true; }
            Point point = Native.CursorPoint();
            if (point.X >= 0 && point.X < 5 && point.Y >= 0 && point.Y < 5)
            { reason = "已触发左上角紧急停止"; return true; }
            return false;
        }

        internal void Stop(string message)
        {
            stop.Set();
            if (worker != null && worker.IsAlive) worker.Join();
            worker = null;
            lock (sync)
            {
                if (message != null) reason = message;
                running = false; paused = false; watch.Stop(); remaining = 0;
            }
        }
        public void Dispose() { Stop("已停止"); stop.Dispose(); }
    }
}
