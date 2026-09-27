// Deterministic tests of the production ClickEngine. Only the OS input boundary
// is replaced; these tests never move the desktop cursor or click another app.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace MintClicker
{
    internal static class Native
    {
        internal static readonly object Gate = new object();
        internal static readonly List<Point> Received = new List<Point>();
        internal static readonly List<bool> Doubles = new List<bool>();
        internal static readonly List<int> Buttons = new List<int>();
        internal static readonly ManualResetEvent FirstClick = new ManualResetEvent(false);
        internal static volatile bool StopKey, Corner;
        internal static Point? Blocked;
        internal static Point CursorPoint() { return Corner ? Point.Empty : new Point(100, 100); }
        internal static short GetAsyncKeyState(int key) { return StopKey ? unchecked((short)0x8000) : (short)0; }
        internal static bool IsOwnWindow(Point point, IntPtr handle) { return Blocked.HasValue && point == Blocked.Value; }
        internal static void Click(int button, bool twice, Point? point, Rectangle desktop)
        {
            lock (Gate) { Received.Add(point ?? CursorPoint()); Doubles.Add(twice); Buttons.Add(button); }
            FirstClick.Set();
        }
        internal static void Reset()
        {
            lock (Gate) { Received.Clear(); Doubles.Clear(); Buttons.Clear(); }
            FirstClick.Reset(); StopKey = Corner = false; Blocked = null;
        }
    }

    internal static class RouteTests
    {
        private static readonly Point A = new Point(200, 100), B = new Point(400, 100), C = new Point(600, 100);
        private static int failures;
        private static ClickOptions Options(Point[] route, long limit)
        {
            return new ClickOptions { Interval = 10, Button = 0, Positions = route, Limit = limit, Desktop = new Rectangle(0, 0, 1920, 1080) };
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Wait(ClickEngine engine)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (engine.Running && elapsed.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert(!engine.Running, "engine timed out");
        }
        private static void Sequence(params Point[] expected)
        {
            lock (Native.Gate)
            {
                Assert(Native.Received.Count == expected.Length, "wrong action count: " + Native.Received.Count);
                for (int i = 0; i < expected.Length; i++) Assert(Native.Received[i] == expected[i], "wrong position at index " + i);
            }
        }
        private static void Check(string name, Action<ClickEngine> test)
        {
            Native.Reset();
            using (ClickEngine engine = new ClickEngine())
            {
                try { test(engine); Console.WriteLine("PASS " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
            }
        }
        private static int Main()
        {
            Check("A-B-C twice counts two complete cycles", delegate(ClickEngine engine)
            { engine.Start(Options(new[] { A, B, C }, 2)); Wait(engine); Sequence(A, B, C, A, B, C); Assert(engine.Rounds == 2, "wrong cycles"); });
            Check("selected subset retains supplied order", delegate(ClickEngine engine)
            { engine.Start(Options(new[] { C, A }, 2)); Wait(engine); Sequence(C, A, C, A); Assert(engine.Rounds == 2, "wrong cycles"); });
            Check("single selected position", delegate(ClickEngine engine)
            { engine.Start(Options(new[] { B }, 3)); Wait(engine); Sequence(B, B, B); Assert(engine.Rounds == 3, "wrong cycles"); });
            Check("double click flag applies to every position", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, C }, 2); o.DoubleClick = true; engine.Start(o); Wait(engine); Sequence(A, C, A, C); Assert(Native.Doubles.TrueForAll(delegate(bool value) { return value; }), "double flag missing"); Assert(engine.Rounds == 2, "wrong cycles"); });
            Check("stop in a long wait leaves incomplete cycle uncounted", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B, C }, 0); o.Interval = 30000; engine.Start(o); Assert(Native.FirstClick.WaitOne(1000), "no first click"); Stopwatch elapsed = Stopwatch.StartNew(); engine.Stop("test stop"); Assert(elapsed.ElapsedMilliseconds < 250, "slow cancellation"); Sequence(A); Assert(engine.Rounds == 0, "partial cycle counted"); });
            Check("restart begins at first position", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B }, 0); o.Interval = 30000; engine.Start(o); Assert(Native.FirstClick.WaitOne(1000), "no first click"); engine.Stop("restart"); Native.Reset(); engine.Start(Options(new[] { A, B }, 1)); Wait(engine); Sequence(A, B); Assert(engine.Rounds == 1, "rounds not reset"); });
            Check("active route is isolated from caller edits", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B, C }, 2); engine.Start(o); o.Positions[1] = C; o.Limit = 20; Wait(engine); Sequence(A, B, C, A, B, C); });
            Check("empty route rejected", delegate(ClickEngine engine)
            { bool rejected = false; try { engine.Start(Options(new Point[0], 1)); } catch (ArgumentException) { rejected = true; } Assert(rejected, "empty route accepted"); Sequence(); });
            Check("own-window protection applies at every position", delegate(ClickEngine engine)
            { Native.Blocked = B; engine.Start(Options(new[] { A, B, C }, 1)); Wait(engine); Sequence(A); Assert(engine.Rounds == 0, "partial cycle counted"); });
            Check("F8 polling cancels multi-point long wait", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B }, 0); o.Interval = 30000; engine.Start(o); Assert(Native.FirstClick.WaitOne(1000), "no first click"); Native.StopKey = true; Wait(engine); Sequence(A); Assert(engine.Rounds == 0, "partial cycle counted"); });
            Check("corner emergency cancels multi-point long wait", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B }, 0); o.Interval = 30000; engine.Start(o); Assert(Native.FirstClick.WaitOne(1000), "no first click"); Native.Corner = true; Wait(engine); Sequence(A); });
            Check("legacy fixed-point mode", delegate(ClickEngine engine)
            { ClickOptions o = Options(null, 2); o.FixedPoint = B; engine.Start(o); Wait(engine); Sequence(B, B); Assert(engine.Rounds == 2, "wrong legacy count"); });
            Check("legacy follow-cursor mode", delegate(ClickEngine engine)
            { engine.Start(Options(null, 2)); Wait(engine); Sequence(new Point(100, 100), new Point(100, 100)); Assert(engine.Rounds == 2, "wrong legacy count"); });
            Check("mixed left-right-middle follows each point", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B, C }, 2); o.PositionButtons = new[] { 0, 1, 2 }; engine.Start(o); Wait(engine); Sequence(A, B, C, A, B, C); Assert(string.Join(",", Native.Buttons) == "0,1,2,0,1,2", "wrong mouse buttons"); });
            Check("button snapshot isolated from caller edits", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B }, 2); o.PositionButtons = new[] { 1, 2 }; engine.Start(o); o.PositionButtons[1] = 0; Wait(engine); Assert(string.Join(",", Native.Buttons) == "1,2,1,2", "mutable button route"); });
            Check("mismatched point button count rejected", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A, B }, 1); o.PositionButtons = new[] { 0 }; bool rejected = false; try { engine.Start(o); } catch (ArgumentException) { rejected = true; } Assert(rejected, "mismatch accepted"); });
            Check("invalid point button rejected", delegate(ClickEngine engine)
            { ClickOptions o = Options(new[] { A }, 1); o.PositionButtons = new[] { 3 }; bool rejected = false; try { engine.Start(o); } catch (ArgumentException) { rejected = true; } Assert(rejected, "invalid button accepted"); });
            Console.WriteLine(failures == 0 ? "17 / 17 deterministic tests passed" : failures + " tests failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
