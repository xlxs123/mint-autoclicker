using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace MintClicker
{
    // Console test runner for the engine and its surrounding pure logic. Built by
    // "build.ps1 -RunTests"; exits non-zero when anything fails.
    internal static class EngineTests
    {
        private static int failures;
        private static int checks;

        private static int Main()
        {
            Run("single point honours the round limit", SinglePointRoundLimit);
            Run("per-point repeat count", RepeatCountPerPoint);
            Run("local loop order", LocalLoopOrder);
            Run("pause freezes progress and resume finishes", PauseResume);
            Run("stop halts the worker", StopHalts);
            Run("target guard pauses and resumes", TargetGuard);
            Run("pixel wait times out and stops", PixelTimeout);
            Run("pixel wait succeeds then continues", PixelSuccess);
            Run("position jitter stays inside the window", PositionJitter);
            Run("run time limit stops the engine", RunTimeLimit);
            Run("engine validation rejects bad options", ValidationRejects);
            Run("recorded events convert to steps", RecorderConversion);
            Run("drag step dispatches a drag", DragDispatch);
            Run("relative coordinates resolve against the target window", RelativeCoordinates);
            Run("template match finds the image", TemplateMatch);
            Run("find image waits and clicks the match", FindImageClick);
            Run("find image times out and stops", FindImageTimeout);
            Run("run report formats the summary", RunReportFormat);
            Run("auto-save writes the edited profile", AutoSaveTest);
            Run("scheduled task arguments are correct", ScheduledTaskArguments);
            Run("target check rejects unsafe points", TargetChecks);
            Run("diagnostics zip contains info, profile and log", DiagnosticsZip);
            Run("main window layout fits", MainWindowLayout);
            Run("options dialog layout fits", OptionsDialogLayout);
            Run("action editor layout fits", ActionEditorLayout);
            Run("hotkey dialog layout fits", HotkeyDialogLayout);
            Run("schedule dialog layout fits", ScheduleDialogLayout);
            Console.WriteLine(failures == 0 ? "ALL " + checks + " CHECKS PASSED" : failures + " FAILURES");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("FAIL " + name + "：" + error.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        private static void WaitFor(Func<bool> condition, int timeout, string message)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeout)
            {
                if (condition()) return;
                Thread.Sleep(10);
            }
            throw new Exception(message);
        }

        private static ClickEngine NewEngine(FakeInput input)
        {
            ClickEngine engine = new ClickEngine();
            engine.Input = input;
            return engine;
        }

        private static ClickOptions Options(params Point[] points)
        {
            return new ClickOptions
            {
                Interval = 20,
                Button = 0,
                Positions = points,
                Desktop = new Rectangle(0, 0, 1920, 1080),
                MainWindow = new IntPtr(42)
            };
        }

        private static void SinglePointRoundLimit()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100));
            options.Limit = 2;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.ClickCount == 2, "expected 2 clicks, got " + input.ClickCount);
                Assert(engine.Rounds == 2, "expected 2 rounds, got " + engine.Rounds);
                Assert(engine.Actions == 2, "expected 2 dispatched actions, got " + engine.Actions);
                Assert(engine.Reason.Contains("已完成设定轮数"), "reason was " + engine.Reason);
                Assert(input.PrecisionChanges == 2, "precision should be raised and released once, got " + input.PrecisionChanges);
            }
        }

        private static void RepeatCountPerPoint()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(150, 150));
            options.Limit = 1;
            options.RepeatCounts = new[] { 3 };
            options.RepeatIntervals = new[] { 5 };
            options.DelaysAfter = new[] { 5 };
            options.SourceNumbers = new[] { 1 };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.ClickCount == 3, "expected 3 clicks, got " + input.ClickCount);
            }
        }

        private static void LocalLoopOrder()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(10, 10), new Point(20, 20), new Point(30, 30), new Point(40, 40));
            options.Interval = 10;
            options.DelaysAfter = new[] { 5, 5, 5, 5 };
            options.LoopStart = 1;
            options.LoopEnd = 2;
            options.LoopCount = 2;
            options.Limit = 1;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Point[] points = input.ClickPoints;
                string order = string.Join(" ", Array.ConvertAll(points, delegate(Point p) { return p.X.ToString(); }));
                Assert(order == "10 20 30 20 30 40", "click order was " + order);
            }
        }

        private static void PauseResume()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100), new Point(110, 110));
            options.Interval = 150;
            options.Limit = 1;
            options.DelaysAfter = new[] { 150, 150 };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return input.ClickCount >= 1; }, 3000, "first click did not happen");
                engine.TogglePause();
                Assert(engine.Paused, "engine did not pause");
                int frozen = input.ClickCount;
                Thread.Sleep(350);
                Assert(input.ClickCount == frozen, "clicks continued while paused (" + frozen + " -> " + input.ClickCount + ")");
                engine.TogglePause();
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish after resume");
                Assert(input.ClickCount == 2, "expected 2 clicks, got " + input.ClickCount);
            }
        }

        private static void StopHalts()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100));
            options.Interval = 100;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return input.ClickCount >= 1; }, 3000, "first click did not happen");
                engine.Stop("测试停止");
                int stopped = input.ClickCount;
                Thread.Sleep(200);
                Assert(!engine.Running, "engine still running after Stop");
                Assert(input.ClickCount == stopped, "clicks continued after Stop");
            }
        }

        private static void TargetGuard()
        {
            FakeInput input = new FakeInput { TargetForeground = false };
            ClickOptions options = Options(new Point(100, 100));
            options.Interval = 30;
            options.TargetWindow = new IntPtr(7);
            options.TargetProcessId = 5;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                Thread.Sleep(150);
                Assert(input.ClickCount == 0, "clicks happened without the target being foreground");
                Assert(engine.Paused && engine.AutoPaused, "target guard did not pause the run");
                input.TargetForeground = true;
                engine.TogglePause();
                WaitFor(delegate { return input.ClickCount >= 1; }, 3000, "run did not resume after the target returned");
                engine.Stop("done");
            }
        }

        private static void PixelTimeout()
        {
            FakeInput input = new FakeInput();
            input.PixelSource = delegate { return Color.Red; };
            ClickOptions options = Options();
            options.Interval = 20;
            options.Limit = 1;
            options.Actions = new[]
            {
                new MacroEntry { Kind = ActionKind.Pixel, X = 10, Y = 10, PixelColor = Color.Lime.ToArgb(), PixelTolerance = 0, Duration = 200 },
                new MacroEntry { Kind = ActionKind.Click, X = 20, Y = 20 }
            };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.ClickCount == 0, "click should not happen after a colour timeout");
                Assert(engine.Reason.Contains("等待颜色超时"), "reason was " + engine.Reason);
            }
        }

        private static void PixelSuccess()
        {
            FakeInput input = new FakeInput();
            int reads = 0;
            input.PixelSource = delegate
            {
                reads++;
                return reads < 3 ? Color.Red : Color.Lime;
            };
            ClickOptions options = Options();
            options.Interval = 20;
            options.Limit = 1;
            options.Actions = new[]
            {
                new MacroEntry { Kind = ActionKind.Pixel, X = 10, Y = 10, PixelColor = Color.Lime.ToArgb(), PixelTolerance = 0, Duration = 3000 },
                new MacroEntry { Kind = ActionKind.Click, X = 20, Y = 20 }
            };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.ClickCount == 1, "expected 1 click after the colour matched, got " + input.ClickCount);
            }
        }

        private static void PositionJitter()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100));
            options.Limit = 1;
            options.JitterPosition = 5;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Point point = input.ClickPoints[0];
                Assert(Math.Abs(point.X - 100) <= 5 && Math.Abs(point.Y - 100) <= 5, "jittered point was " + point);
            }
        }

        private static void RunTimeLimit()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100), new Point(200, 200));
            options.Interval = 200;
            options.DelaysAfter = new[] { 200, 200 };
            options.TimeLimitMs = 150;
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(engine.Reason.Contains("已达到运行时长"), "reason was " + engine.Reason);
                Assert(input.ClickCount <= 1, "expected at most 1 click before the limit, got " + input.ClickCount);
            }
        }

        private static void TemplateMatch()
        {
            const int screenWidth = 40, screenHeight = 30, width = 10, height = 8;
            int[] screen = new int[screenWidth * screenHeight];
            int[] template = new int[width * height];
            for (int i = 0; i < template.Length; i++) template[i] = unchecked((int)0xFF336699);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    screen[(7 + y) * screenWidth + 12 + x] = template[y * width + x];

            Point location;
            Assert(TemplateMatcher.Find(screen, screenWidth, screenHeight, template, width, height, 0, out location), "template was not found");
            Assert(location == new Point(12, 7), "match location was " + location);

            screen[7 * screenWidth + 12] = unchecked((int)0xFF33669B);
            Assert(!TemplateMatcher.Find(screen, screenWidth, screenHeight, template, width, height, 0, out location), "a shifted pixel matched with tolerance 0");
            Assert(TemplateMatcher.Find(screen, screenWidth, screenHeight, template, width, height, 4, out location), "a 2-level pixel shift should match with tolerance 4");

            Point? center = TemplateMatcher.FindCenter(screen, screenWidth, screenHeight, template, width, height, 4);
            Assert(center.HasValue && center.Value == new Point(12 + width / 2, 7 + height / 2), "match centre was wrong");
        }

        private static void FindImageClick()
        {
            const int screenWidth = 60, screenHeight = 50, width = 10, height = 8;
            int[] screen = new int[screenWidth * screenHeight];
            int[] template = new int[width * height];
            for (int i = 0; i < template.Length; i++) template[i] = unchecked((int)0xFF336699);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    screen[(14 + y) * screenWidth + 20 + x] = template[y * width + x];

            FakeInput input = new FakeInput { CapturePixels = screen };
            ClickOptions options = Options();
            options.Desktop = new Rectangle(0, 0, screenWidth, screenHeight);
            options.Limit = 1;
            options.Actions = new[]
            {
                new MacroEntry { Kind = ActionKind.FindImage, Template = TemplateImage.Encode(template, width, height), PixelTolerance = 0, Duration = 2000, Button = 0 }
            };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 8000, "run did not finish");
                Assert(input.ClickPoints.Length == 1, "expected one click, got " + input.ClickPoints.Length);
                Point expected = new Point(20 + width / 2, 14 + height / 2);
                Assert(input.ClickPoints[0] == expected, "clicked at " + input.ClickPoints[0]);
                Assert(engine.Reason.Contains("已完成设定轮数"), "reason was " + engine.Reason);
            }
        }

        private static void FindImageTimeout()
        {
            const int screenWidth = 40, screenHeight = 30, width = 10, height = 8;
            int[] template = new int[width * height];
            for (int i = 0; i < template.Length; i++) template[i] = unchecked((int)0xFF336699);

            FakeInput input = new FakeInput { CapturePixels = new int[screenWidth * screenHeight] };
            ClickOptions options = Options();
            options.Desktop = new Rectangle(0, 0, screenWidth, screenHeight);
            options.Limit = 1;
            options.Actions = new[]
            {
                new MacroEntry { Kind = ActionKind.FindImage, Template = TemplateImage.Encode(template, width, height), PixelTolerance = 0, Duration = 200 },
                new MacroEntry { Kind = ActionKind.Click, X = 20, Y = 20 }
            };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 6000, "run did not finish");
                Assert(input.ClickCount == 0, "click should not happen after an image timeout");
                Assert(engine.Reason.Contains("等待图片超时"), "reason was " + engine.Reason);
            }
        }

        private static void RunReportFormat()
        {
            RunReport report = new RunReport
            {
                Started = new DateTime(2026, 10, 18, 10, 0, 0), Duration = new TimeSpan(0, 2, 5),
                Rounds = 12, Actions = 340, Reason = "已完成设定轮数", Profile = "默认方案", Mode = "多点循环"
            };
            Assert(report.Summary.Contains("12 轮"), "summary was " + report.Summary);
            Assert(report.Summary.Contains("340 次动作"), "summary was " + report.Summary);
            Assert(report.Summary.Contains("02:05"), "summary was " + report.Summary);
            Assert(report.Rate == "2.7 次/秒", "rate was " + report.Rate);

            string text = report.Describe();
            Assert(text.Contains("默认方案") && text.Contains("多点循环") && text.Contains("340") && text.Contains("已完成设定轮数"), "report text was " + text);
            Assert(RunReport.FormatDuration(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)) == "01:02:03", "hour formatting was wrong");
            Assert(RunReport.FormatDuration(TimeSpan.FromSeconds(59)) == "00:59", "minute formatting was wrong");
        }

        private static void AutoSaveTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "mint-autosave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string settings = Path.Combine(root, "settings.xml");
                string library = Path.Combine(root, "settings.profiles.v2.json");
                using (MainForm form = new MainForm(settings, false))
                {
                    Assert(!form.AutoSaveNow(), "a clean window must not auto-save");
                    NumericUpDown interval = (NumericUpDown)typeof(MainForm).GetField("interval", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    Assert(interval != null, "interval control not found");
                    // A real control edit: the dirty hook must notice it.
                    interval.Value = 500;
                    Assert(form.AutoSaveNow(), "an edited window should auto-save");
                    Assert(File.Exists(library), "auto-save did not write the library file");
                    string json = File.ReadAllText(library);
                    Assert(json.Contains("\"Interval\":500"), "saved interval was not 500");
                    Assert(!form.AutoSaveNow(), "auto-save should clear the dirty flag");
                }

                // Timer path: the UI timer, not just the direct call, must persist edits.
                string timerLibrary = Path.Combine(root, "timer.profiles.v2.json");
                string timerSettings = Path.Combine(root, "timer.xml");
                using (MainForm form = new MainForm(timerSettings, false))
                {
                    form.AutoSaveSeconds = 1;
                    form.Show();
                    NumericUpDown interval = (NumericUpDown)typeof(MainForm).GetField("interval", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    interval.Value = 750;
                    DateTime deadline = DateTime.Now.AddSeconds(3);
                    while (DateTime.Now < deadline)
                    {
                        Application.DoEvents();
                        Thread.Sleep(15);
                    }
                    form.Hide();
                    Assert(File.Exists(timerLibrary), "the UI timer did not auto-save");
                    Assert(File.ReadAllText(timerLibrary).Contains("\"Interval\":750"), "timer-based auto-save wrote the wrong interval");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }

        private static void ScheduleDialogLayout()
        {
            using (ScheduleDialog dialog = new ScheduleDialog()) AuditLayout(dialog);
        }

        private static void ScheduledTaskArguments()
        {
            string daily = ScheduledTask.BuildCreateArguments(@"C:\tools\click.exe", new DateTime(2026, 10, 19, 9, 30, 0), true);
            Assert(daily.Contains("/SC DAILY"), "daily schedule missing: " + daily);
            Assert(daily.Contains("/ST 09:30"), "daily time missing: " + daily);
            Assert(daily.Contains("/TN \"" + ScheduledTask.TaskName + "\""), "task name missing: " + daily);
            Assert(daily.Contains("/TR \"C:\\tools\\click.exe\""), "target missing: " + daily);

            string once = ScheduledTask.BuildCreateArguments(@"C:\Program Files\轻点\AutoClicker.exe", new DateTime(2026, 10, 19, 9, 30, 0), false);
            Assert(once.Contains("/SC ONCE") && once.Contains("/SD 2026/10/19") && once.Contains("/ST 09:30"), "once schedule wrong: " + once);
            Assert(once.Contains("\"\\\"C:\\Program Files\\轻点\\AutoClicker.exe\\\"\""), "quoted target wrong: " + once);

            string delete = ScheduledTask.BuildDeleteArguments();
            Assert(delete.StartsWith("/Delete /F /TN") && delete.Contains(ScheduledTask.TaskName), "delete arguments wrong: " + delete);
            Assert(ScheduledTask.BuildQueryArguments().StartsWith("/Query"), "query arguments wrong");
        }

        private static void ValidationRejects()
        {
            FakeInput input = new FakeInput();
            using (ClickEngine engine = NewEngine(input))
            {
                ClickOptions fast = Options(new Point(100, 100));
                fast.Interval = 5;
                bool rejected = false;
                try { engine.Start(fast); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected, "interval 5 was accepted");

                ClickOptions tooMany = Options(new Point[1001]);
                for (int i = 0; i < tooMany.Positions.Length; i++) tooMany.Positions[i] = new Point(10 + i, 10);
                rejected = false;
                try { engine.Start(tooMany); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "1001 positions were accepted");

                ClickOptions badLoop = Options(new Point(10, 10), new Point(20, 20));
                badLoop.LoopStart = 1; badLoop.LoopEnd = 5; badLoop.LoopCount = 2;
                rejected = false;
                try { engine.Start(badLoop); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "an out-of-range loop was accepted");

                ClickOptions missingTemplate = Options(new Point(10, 10));
                missingTemplate.Actions = new[] { new MacroEntry { Kind = ActionKind.FindImage, Template = "" } };
                rejected = false;
                try { engine.Start(missingTemplate); }
                catch (System.IO.InvalidDataException) { rejected = true; }
                Assert(rejected, "a find-image step without a template was accepted");
            }
        }

        private static void RelativeCoordinates()
        {
            FakeInput input = new FakeInput { ClientOriginValue = new Point(1000, 500) };
            ClickOptions options = Options(new Point(10, 20));
            options.Limit = 1;
            options.RelativeToTarget = true;
            options.TargetWindow = new IntPtr(7);
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.ClickPoints.Length == 1, "expected one click");
                Assert(input.ClickPoints[0] == new Point(1010, 520), "click was at " + input.ClickPoints[0]);
            }
        }

        private static void DragDispatch()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options();
            options.Interval = 20;
            options.Limit = 1;
            options.Actions = new[]
            {
                new MacroEntry { Kind = ActionKind.Drag, X = 10, Y = 10, EndX = 210, EndY = 160, Button = 0, Duration = 120 }
            };
            using (ClickEngine engine = NewEngine(input))
            {
                engine.Start(options);
                WaitFor(delegate { return !engine.Running; }, 5000, "run did not finish");
                Assert(input.DragCount == 1, "expected one drag, got " + input.DragCount);
                Assert(input.LastDragFrom == new Point(10, 10) && input.LastDragTo == new Point(210, 160), "drag coordinates were wrong");
                Assert(input.LastDragMillis == 120, "drag duration was " + input.LastDragMillis);
            }
        }

        private static void RecorderConversion()
        {
            List<Recorder.RecordedEvent> doubleClick = new List<Recorder.RecordedEvent>
            {
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Click, X = 100, Y = 100, Button = 0, Time = 0 },
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Click, X = 101, Y = 100, Button = 0, Time = 150 }
            };
            List<MacroEntry> steps = Recorder.Convert(doubleClick);
            Assert(steps.Count == 1 && steps[0].Kind == ActionKind.Click && steps[0].DoubleClick, "two fast clicks did not become a double click");

            List<Recorder.RecordedEvent> spaced = new List<Recorder.RecordedEvent>
            {
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Click, X = 100, Y = 100, Button = 0, Time = 0 },
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Click, X = 200, Y = 200, Button = 1, Time = 600 }
            };
            steps = Recorder.Convert(spaced);
            Assert(steps.Count == 3, "expected click, wait, click; got " + steps.Count + " steps");
            Assert(steps[1].Kind == ActionKind.Wait && steps[1].Duration == 600, "gap was not converted to a 600 ms wait");
            Assert(steps[2].Button == 1, "right click was not kept");

            List<Recorder.RecordedEvent> mixed = new List<Recorder.RecordedEvent>
            {
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Key, Key = 65, Control = true, Time = 0 },
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Scroll, X = 50, Y = 60, Wheel = 2, Time = 500 }
            };
            steps = Recorder.Convert(mixed);
            Assert(steps.Count == 3, "expected key, wait, scroll; got " + steps.Count);
            Assert(steps[0].Kind == ActionKind.Key && steps[0].Control && steps[0].KeyCode == 65, "key step was not kept");
            Assert(steps[2].Kind == ActionKind.Scroll && steps[2].Wheel == 2, "scroll step was not kept");

            List<Recorder.RecordedEvent> drag = new List<Recorder.RecordedEvent>
            {
                new Recorder.RecordedEvent { Kind = Recorder.RecordedEventKind.Drag, X = 10, Y = 20, EndX = 100, EndY = 200, Button = 0, Duration = 350, Time = 0 }
            };
            steps = Recorder.Convert(drag);
            Assert(steps.Count == 1 && steps[0].Kind == ActionKind.Drag, "drag event did not become a drag step");
            Assert(steps[0].EndX == 100 && steps[0].EndY == 200 && steps[0].Duration == 350, "drag step lost its end point or duration");
        }

        private static void TargetChecks()
        {
            FakeInput input = new FakeInput();
            ClickOptions options = Options(new Point(100, 100));
            ActionCompiler.CheckTarget(new Point(100, 100), options, input);

            input.OwnWindow = true;
            bool rejected = false;
            try { ActionCompiler.CheckTarget(new Point(100, 100), options, input); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "a point over the clicker window was accepted");

            input.OwnWindow = false;
            input.OnScreen = false;
            rejected = false;
            try { ActionCompiler.CheckTarget(new Point(100, 100), options, input); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "an off-screen point was accepted");

            input.OnScreen = true;
            rejected = false;
            try { ActionCompiler.CheckTarget(new Point(2, 2), options, input); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "the emergency corner was accepted");
        }

        private static void MainWindowLayout()
        {
            string root = Path.Combine(Path.GetTempPath(), "mint-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using (MainForm form = new MainForm(Path.Combine(root, "settings.xml"), false))
                    AuditLayout(form);
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }

        private static void OptionsDialogLayout()
        {
            using (OptionsDialog dialog = new OptionsDialog(new Preferences(), false, false))
                AuditLayout(dialog);
        }

        private static void ActionEditorLayout()
        {
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Click })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Wait })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Key })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Scroll })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Pixel })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.Drag })) AuditLayout(editor);
            using (ActionEditor editor = new ActionEditor(new MacroEntry { Kind = ActionKind.FindImage })) AuditLayout(editor);
        }

        private static void HotkeyDialogLayout()
        {
            using (HotkeyDialog dialog = new HotkeyDialog(new Preferences()))
                AuditLayout(dialog);
        }

        // Builds the form off-screen (needed because Control.Visible follows the
        // parent chain), then checks every visible control for clipped text, overflow
        // and overlapping siblings.
        private static void AuditLayout(Form form)
        {
            bool wasVisible = form.Visible;
            List<string> issues = new List<string>();
            try
            {
                if (!wasVisible)
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000);
                    form.Show();
                }
                form.CreateControl();
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                    Console.WriteLine("    环境: DPI=" + graphics.DpiX + " 主屏=" + Screen.PrimaryScreen.Bounds +
                        " 工作区=" + Screen.PrimaryScreen.WorkingArea + " 字体=" + form.Font.FontFamily.Name + " " + form.Font.SizeInPoints.ToString("0.#") + "pt");
                AuditContainer(form, issues);
            }
            finally
            {
                if (!wasVisible) form.Hide();
            }
            foreach (string issue in issues) Console.WriteLine("    布局: " + issue);
            Assert(issues.Count == 0, form.Text + " ClientSize=" + form.ClientSize + " MinimumSize=" + form.MinimumSize + " 有 " + issues.Count + " 处布局问题");
        }

        private static void AuditContainer(Control parent, List<string> issues)
        {
            List<Control> leaves = new List<Control>();
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (child.Left < -1 || child.Top < -1 || child.Right > parent.ClientSize.Width + 2 || child.Bottom > parent.ClientSize.Height + 2)
                    issues.Add(Describe(child) + " 超出 " + parent.GetType().Name + " 边界");
                if (IsContainer(child)) AuditContainer(child, issues);
                else
                {
                    CheckText(child, issues);
                    leaves.Add(child);
                }
            }
            for (int i = 0; i < leaves.Count; i++)
                for (int j = i + 1; j < leaves.Count; j++)
                {
                    Rectangle overlap = Rectangle.Intersect(TextBounds(leaves[i]), TextBounds(leaves[j]));
                    if (overlap.Width > 3 && overlap.Height > 3)
                        issues.Add("重叠: " + Describe(leaves[i]) + " / " + Describe(leaves[j]));
                }
        }

        // A label's text is vertically centred, so its painted text can be smaller than
        // the control. Overlap checks use the painted area, not the full control box.
        private static Rectangle TextBounds(Control control)
        {
            Label label = control as Label;
            if (label == null || label.AutoSize) return control.Bounds;
            Size text = MeasuredText(label);
            int width = Math.Min(label.Width, Math.Max(1, text.Width));
            int height = Math.Min(label.Height, Math.Max(1, text.Height));
            return new Rectangle(label.Left, label.Top + (label.Height - height) / 2, width, height);
        }

        private static Size MeasuredText(Control control)
        {
            Label label = control as Label;
            if (label != null && !label.AutoEllipsis)
                return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.ClientSize.Width), Int32.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            return TextRenderer.MeasureText(control.Text, control.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private static bool IsContainer(Control control)
        {
            return control is Form || control is Panel || control is TabControl || control is TabPage || control is GroupBox;
        }

        private static void CheckText(Control child, List<string> issues)
        {
            if (child is ComboBox || child is NumericUpDown || child is TextBox || child is ListView || child is DateTimePicker) return;
            string text = child.Text;
            if (String.IsNullOrEmpty(text)) return;
            Size client = child.ClientSize;
            if (client.Width <= 0 || client.Height <= 0) return;
            Label label = child as Label;
            if (label != null)
            {
                if (label.AutoSize) return;
                Size measured = MeasuredText(label);
                if (measured.Height > client.Height + 4)
                    issues.Add(Describe(child) + " 文字高 " + measured.Height + " > 控件高 " + client.Height);
                return;
            }
            if (child is Button || child is CheckBox || child is RadioButton)
            {
                Size measured = TextRenderer.MeasureText(text, child.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int padding = child is CheckBox ? 24 : 10;
                if (measured.Width > client.Width - padding + 4)
                    issues.Add(Describe(child) + " 文字宽 " + measured.Width + " > 可用宽 " + (client.Width - padding));
                if (measured.Height > client.Height + 4)
                    issues.Add(Describe(child) + " 文字高 " + measured.Height + " > 控件高 " + client.Height);
            }
        }

        private static string Describe(Control control)
        {
            string text = control.Text ?? "";
            if (text.Length > 18) text = text.Substring(0, 18) + "…";
            return control.GetType().Name + "[" + text.Replace("\n", " ") + "]"
                + " font=" + control.Font.SizeInPoints.ToString("0.#") + "pt/" + control.Font.Height + "px"
                + " h=" + control.Height;
        }

        private static void DiagnosticsZip()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mint-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string profiles = Path.Combine(folder, "settings.profiles.v2.json");
                File.WriteAllText(profiles, "{}");
                string logs = Path.Combine(folder, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "app.log"), "hello");
                string zipPath = Path.Combine(folder, "diag.zip");
                Diagnostics.Export(zipPath, "info text", profiles, logs);
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    List<string> names = new List<string>();
                    foreach (ZipArchiveEntry entry in zip.Entries) names.Add(entry.FullName);
                    Assert(names.Contains("info.txt"), "info.txt missing from the archive");
                    Assert(names.Contains("settings.profiles.v2.json"), "profile copy missing from the archive");
                    Assert(names.Contains("logs/app.log"), "log copy missing from the archive");
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (Exception) { }
            }
        }
    }

    // Records what the engine would have done on the real desktop.
    internal sealed class FakeInput : IInput
    {
        private readonly object sync = new object();
        private readonly List<Point> clicks = new List<Point>();
        internal Point Cursor = new Point(500, 500);
        internal bool OwnWindow, OnScreen = true, TargetForeground = true;
        internal Func<Point, Color> PixelSource = delegate { return Color.Black; };
        internal int PrecisionChanges;
        internal int DragCount;
        internal Point LastDragFrom, LastDragTo;
        internal int LastDragMillis;
        internal Point ClientOriginValue = new Point(0, 0);
        internal bool ClientOriginValid = true;
        internal Rectangle ClientAreaValue = Rectangle.Empty;
        internal bool ClientAreaValid = true;
        internal int[] CapturePixels;

        internal int ClickCount { get { lock (sync) return clicks.Count; } }
        internal Point[] ClickPoints { get { lock (sync) return clicks.ToArray(); } }

        public void Click(int button, bool twice, Point? fixedPoint, Rectangle desktop)
        {
            lock (sync) clicks.Add(fixedPoint ?? Cursor);
        }
        public void Move(Point point, Rectangle desktop) { }
        public void Scroll(Point point, int notches, Rectangle desktop) { }
        public void Drag(int button, Point from, Point to, int milliseconds, Rectangle desktop)
        {
            DragCount++;
            LastDragFrom = from;
            LastDragTo = to;
            LastDragMillis = milliseconds;
        }
        public void KeyPress(int code, bool control, bool alt, bool shift) { }
        public Point CursorPoint() { return Cursor; }
        public Color Pixel(Point point) { return PixelSource(point); }
        public short KeyState(int key) { return 0; }
        public IntPtr ForegroundWindow() { return new IntPtr(42); }
        public bool IsOwnWindow(Point point, IntPtr mainWindow) { return OwnWindow; }
        public bool IsOnScreen(Point point) { return OnScreen; }
        public bool IsTargetForeground(IntPtr window, uint processId) { return TargetForeground; }
        public bool ClientOrigin(IntPtr window, out Point origin)
        {
            origin = ClientOriginValid ? ClientOriginValue : Point.Empty;
            return ClientOriginValid;
        }
        public bool ClientArea(IntPtr window, out Rectangle area)
        {
            area = ClientAreaValid ? ClientAreaValue : Rectangle.Empty;
            return ClientAreaValid;
        }
        public int[] CaptureScreen(Rectangle area)
        {
            if (CapturePixels != null) return CapturePixels;
            return new int[Math.Max(1, area.Width * area.Height)];
        }
        public void BeginPrecision() { PrecisionChanges++; }
        public void EndPrecision() { PrecisionChanges++; }
    }
}
