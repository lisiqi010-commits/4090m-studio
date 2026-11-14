using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace GameBoost
{
    internal static class EngineSmoke
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--child")
            {
                Thread.Sleep(TimeSpan.FromMinutes(2));
                return 0;
            }

            Action[] tests =
            {
                PreviewAndPathSelection,
                AppliesToNewInstances,
                PausesWithoutClearingSelection,
                RestoresDeselectedProcess,
                RetriesAfterJournalWriteFailure,
                RestoresOriginalPriority,
                RecoversPriorityFromJournal,
                DropsExitedProcessFromJournal,
                IgnoresJournalEntryForReusedPid,
                PreservesExternalPriorityChange,
                LeavesIdleProcessAlone
            };

            int failures = 0;
            foreach (Action test in tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception error)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + error);
                }
            }
            return failures == 0 ? 0 : 1;
        }

        private static void PreviewAndPathSelection()
        {
            using (var fixture = new ProcessFixture())
            {
                Process selected = fixture.Start("selected");
                Process other = fixture.Start("unselected");
                string selectedPath = fixture.Executable("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);

                var preview = engine.Scan(new[] { selectedPath }, false, false);
                Check(preview.Apps.Exists(app => SamePath(app.Path, selectedPath)), "Preview did not list the selected executable.");
                ExpectPriority(selected, ProcessPriorityClass.Normal);

                var active = engine.Scan(new[] { selectedPath }, false, true);
                ExpectPriority(selected, ProcessPriorityClass.BelowNormal);
                ExpectPriority(other, ProcessPriorityClass.Normal);
                Check(active.AdjustedCount == 1, "The snapshot must report one adjusted process.");
                Check(active.Apps.Exists(app => SamePath(app.Path, selectedPath) && app.IsAdjusted),
                    "The selected application must show as adjusted.");
            }
        }

        private static void AppliesToNewInstances()
        {
            using (var fixture = new ProcessFixture())
            {
                Process first = fixture.Start("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);
                var selected = new[] { fixture.Executable("selected") };
                engine.Scan(selected, false, true);
                Process second = fixture.Start("selected");
                ExpectPriority(second, ProcessPriorityClass.Normal);

                var active = engine.Scan(selected, false, true);
                ExpectPriority(first, ProcessPriorityClass.BelowNormal);
                ExpectPriority(second, ProcessPriorityClass.BelowNormal);
                Check(active.AdjustedCount == 2, "The active count must include both live instances.");
                Check(active.Apps.Exists(app => SamePath(app.Path, selected[0]) && app.ProcessCount == 2),
                    "The list must group both instances under the same path.");
            }
        }

        private static void RestoresOriginalPriority()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected", ProcessPriorityClass.AboveNormal);
                var engine = new OptimizationEngine(fixture.JournalPath);
                engine.Scan(new[] { fixture.Executable("selected") }, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);

                Check(engine.Restore() == 0, "A live process should restore successfully.");
                ExpectPriority(child, ProcessPriorityClass.AboveNormal);
                Check(engine.Restore() == 0, "A second restore should also succeed.");
            }
        }

        private static void PausesWithoutClearingSelection()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);
                var selected = new[] { fixture.Executable("selected") };
                engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);

                var paused = engine.Scan(selected, false, false);
                ExpectPriority(child, ProcessPriorityClass.Normal);
                Check(paused.AdjustedCount == 0, "Pause should restore all selected applications.");

                engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);
            }
        }

        private static void RestoresDeselectedProcess()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);
                engine.Scan(new[] { fixture.Executable("selected") }, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);

                var active = engine.Scan(new string[0], false, true);
                ExpectPriority(child, ProcessPriorityClass.Normal);
                Check(active.AdjustedCount == 0, "A deselected application should no longer show as adjusted.");
            }
        }

        private static void RecoversPriorityFromJournal()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var firstEngine = new OptimizationEngine(fixture.JournalPath);
                firstEngine.Scan(new[] { fixture.Executable("selected") }, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);

                var nextEngine = new OptimizationEngine(fixture.JournalPath);
                Check(nextEngine.Restore() == 0, "A fresh engine should recover the saved priority.");
                ExpectPriority(child, ProcessPriorityClass.Normal);
            }
        }

        private static void RetriesAfterJournalWriteFailure()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var selected = new[] { fixture.Executable("selected") };
                var engine = new OptimizationEngine(fixture.JournalPath);
                string blocker = fixture.JournalPath + ".tmp";
                Directory.CreateDirectory(blocker);

                try
                {
                    engine.Scan(selected, false, true);
                }
                catch (IOException)
                {
                    // Reporting the persistence failure by exception is also valid.
                }
                catch (UnauthorizedAccessException)
                {
                    // Windows can report access denied when a directory occupies the file path.
                }
                finally
                {
                    Directory.Delete(blocker);
                }
                ExpectPriority(child, ProcessPriorityClass.Normal);

                engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);
                Check(engine.Restore() == 0, "A successful retry should still journal the original priority.");
                ExpectPriority(child, ProcessPriorityClass.Normal);
            }
        }

        private static void PreservesExternalPriorityChange()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);
                var selected = new[] { fixture.Executable("selected") };
                engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);
                child.PriorityClass = ProcessPriorityClass.AboveNormal;

                var active = engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.AboveNormal);
                Check(active.AdjustedCount == 0, "The external priority change should override active optimization.");
                engine.Scan(selected, false, true);
                ExpectPriority(child, ProcessPriorityClass.AboveNormal);
                Check(engine.Restore() == 0, "An external priority change should retire the journal entry.");
                ExpectPriority(child, ProcessPriorityClass.AboveNormal);
            }
        }

        private static void DropsExitedProcessFromJournal()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected");
                var engine = new OptimizationEngine(fixture.JournalPath);
                engine.Scan(new[] { fixture.Executable("selected") }, false, true);
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);
                child.Kill();
                child.WaitForExit();

                var nextEngine = new OptimizationEngine(fixture.JournalPath);
                Check(nextEngine.Restore() == 0, "An exited process should not leave a pending restore.");
            }
        }

        private static void LeavesIdleProcessAlone()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected", ProcessPriorityClass.Idle);
                var engine = new OptimizationEngine(fixture.JournalPath);
                var active = engine.Scan(new[] { fixture.Executable("selected") }, false, true);

                ExpectPriority(child, ProcessPriorityClass.Idle);
                Check(active.AdjustedCount == 0, "An idle-priority process should not be reported as adjusted.");
            }
        }

        private static void IgnoresJournalEntryForReusedPid()
        {
            using (var fixture = new ProcessFixture())
            {
                Process child = fixture.Start("selected", ProcessPriorityClass.BelowNormal);
                // Same PID and path with an earlier start time simulates a reused PID.
                long previousStart = child.StartTime.ToUniversalTime().AddMinutes(-1).ToFileTimeUtc();
                File.WriteAllText(fixture.JournalPath, String.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3}", child.Id, previousStart, (uint)ProcessPriorityClass.Normal,
                    fixture.Executable("selected")));

                var engine = new OptimizationEngine(fixture.JournalPath);
                Check(engine.Restore() == 0, "A reused PID should not leave a pending restore.");
                ExpectPriority(child, ProcessPriorityClass.BelowNormal);
            }
        }

        private static bool SamePath(string left, string right)
        {
            return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static void ExpectPriority(Process process, ProcessPriorityClass expected)
        {
            process.Refresh();
            ProcessPriorityClass actual = process.PriorityClass;
            Check(actual == expected, "PID " + process.Id + " expected " + expected + ", got " + actual + ".");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private sealed class ProcessFixture : IDisposable
        {
            private readonly string directory = Path.Combine(Path.GetTempPath(), "GameBoostSmoke-" + Guid.NewGuid().ToString("N"));
            private readonly List<Process> children = new List<Process>();

            internal string JournalPath { get { return Path.Combine(directory, "priorities.txt"); } }

            internal string Executable(string label)
            {
                return Path.Combine(directory, label, "workload.exe");
            }

            internal Process Start(string label, ProcessPriorityClass priority = ProcessPriorityClass.Normal)
            {
                string executable = Executable(label);
                if (!File.Exists(executable))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(executable));
                    File.Copy(Assembly.GetExecutingAssembly().Location, executable);
                }

                var start = new ProcessStartInfo(executable, "--child")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var child = Process.Start(start);
                children.Add(child);
                child.PriorityClass = priority;
                return child;
            }

            public void Dispose()
            {
                foreach (Process child in children)
                {
                    using (child)
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit();
                        }
                    }
                }

                if (!Directory.Exists(directory))
                    return;

                foreach (string subdirectory in Directory.GetDirectories(directory))
                {
                    File.Delete(Path.Combine(subdirectory, "workload.exe"));
                    Directory.Delete(subdirectory);
                }
                foreach (string file in Directory.GetFiles(directory))
                    File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
