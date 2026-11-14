using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RtxHub;

public static class OptimizerIntegrationTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--fixture")
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return 0;
        }

        if (args.Length == 2 && args[0] == "--log")
        {
            var log = new StreamWriter(args[1]) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }

        string folder = Path.Combine(Path.GetTempPath(), "StudioOptimizerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string executable = Path.Combine(folder, "BackgroundFixture.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, executable);
            var settings = new GameBoost.Settings(folder) { IncludeDefaults = false };
            settings.Paths.Add(executable);
            settings.Save();

            using (var fixture = Process.Start(new ProcessStartInfo(executable, "--fixture")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                try
                {
                    fixture.PriorityClass = ProcessPriorityClass.Normal;
                    VerifyFirstLaunch(folder, fixture);
                    VerifyEnabledStartup(folder, fixture);
                }
                finally
                {
                    if (!fixture.HasExited)
                    {
                        fixture.Kill();
                        fixture.WaitForExit();
                    }
                }
            }
            Console.WriteLine("OptimizerIntegrationTests: passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (string file in Directory.GetFiles(folder))
                File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void VerifyFirstLaunch(string folder, Process fixture)
    {
        string lockPath = Path.Combine(folder, "instance.lock");
        RunStudio(folder, false, ExitWithKeyboard, async studio =>
        {
            var open = studio.Controls.Find("OpenGameOptimizer", true).OfType<Button>().SingleOrDefault();
            Check(open != null && open.Enabled, "Studio needs an enabled game optimizer entry point.");
            Check(!IsLocked(lockPath) && Priority(fixture) == ProcessPriorityClass.Normal,
                "Existing standalone settings should not enable monitoring before the first Studio use.");

            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                open.PerformClick();
                Check(GetOptimizerWindow(studio) == null && Priority(fixture) == ProcessPriorityClass.Normal,
                    "The existing standalone owner's lock did not prevent Studio optimization.");
                var studioStatus = (Label)ReadField(studio, "status");
                Check(studioStatus.Text.Contains("已在运行"), "Lock contention should tell the user how to proceed.");
            }

            open.PerformClick();
            Form window = await WaitForWindow(studio);
            Check(studio.Visible && window.Visible && window.Owner == studio && window.TopLevel,
                "Opening the optimizer should display an owned, modeless window.");
            var optimizer = window.Controls.OfType<GameBoost.MainForm>().SingleOrDefault();
            Check(optimizer != null && !optimizer.TopLevel, "The optimizer UI should be embedded in Studio's owned window.");
            var status = optimizer.Controls.Find("OptimizationStatus", true).OfType<Label>().Single();
            await WaitFor(() => status.Text == "优化运行中" && Priority(fixture) == ProcessPriorityClass.BelowNormal,
                "First open did not start the configured optimizer.");
            Check(IsLocked(lockPath), "Studio must hold the shared optimizer lock while monitoring.");
            Check(!VisibleNotificationIcon(optimizer), "Embedded optimization should use only the Studio notification icon.");

            open.PerformClick();
            Check(ReferenceEquals(window, GetOptimizerWindow(studio)), "Repeated open should reuse the same optimizer window.");
            window.Close();
            await WaitFor(() => !window.Visible, "Closing the optimizer did not hide the window.");
            Check(!window.IsDisposed && Priority(fixture) == ProcessPriorityClass.BelowNormal,
                "Closing the optimizer should leave monitoring active.");
            Check(IsLocked(lockPath), "Closing the optimizer window released the lock while monitoring was active.");

            open.PerformClick();
            await WaitFor(() => window.Visible, "Reopening should show the same optimizer window.");
            Check(ReferenceEquals(window, GetOptimizerWindow(studio)), "Reopening created a second optimizer window.");
            window.Close();
            await WaitFor(() => !window.Visible, "The second close should also hide the optimizer window.");
            Console.WriteLine("PASS initial Studio open, reuse, hide and background monitoring");
        });

        Check(Priority(fixture) == ProcessPriorityClass.Normal, "Explicit Studio exit did not restore the background priority.");
        Check(!IsLocked(lockPath), "Explicit Studio exit did not release the shared optimizer lock.");
    }

    private static void VerifyEnabledStartup(string folder, Process fixture)
    {
        string lockPath = Path.Combine(folder, "instance.lock");
        RunStudio(folder, true, ExitFromTray, async studio =>
        {
            await WaitFor(() => !studio.Visible, "The Studio startup flag should hide the shell in its tray.");
            await WaitFor(() => Priority(fixture) == ProcessPriorityClass.BelowNormal,
                "Studio did not restart previously enabled monitoring in the tray.");
            Check(IsLocked(lockPath), "Automatic startup did not hold the shared optimizer lock.");
            Check(!studio.OwnedForms.Any(window => window.Visible), "Tray startup should leave the optimizer window hidden.");
        });
        Check(Priority(fixture) == ProcessPriorityClass.Normal, "Tray exit did not restore startup optimization.");
        Check(!IsLocked(lockPath), "Tray exit did not release the shared optimizer lock.");
        Console.WriteLine("PASS enabled optimizer resumes on tray startup and restores on tray exit");
    }

    private static void RunStudio(string folder, bool startInTray, Action<SimpleHome> exit, Func<SimpleHome, Task> test)
    {
        Exception failure = null;
        bool completed = false;
        using (var studio = new SimpleHome(false, startInTray, folder))
        using (var watchdog = new System.Windows.Forms.Timer { Interval = 35000 })
        {
            studio.StartPosition = FormStartPosition.Manual;
            studio.Location = new Point(-32000, -32000);
            studio.Shown += async delegate
            {
                // The existing injection-retry timer uses the real local Studio profile.
                // Exercise only the optimizer integration in this test.
                ((System.Windows.Forms.Timer)ReadField(studio, "pendingTimer")).Stop();
                try
                {
                    await test(studio);
                    completed = true;
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally { exit(studio); }
            };
            watchdog.Tick += delegate
            {
                failure = new TimeoutException("Timed out waiting for Studio or its optimizer to close.");
                Application.ExitThread();
            };
            watchdog.Start();
            Application.Run(studio);
            if (failure != null || !completed)
                throw new InvalidOperationException("Studio optimizer lifecycle failed.", failure);
            Check(studio.IsDisposed, "Studio's explicit Exit command did not dispose the Studio window.");
        }
    }

    private static void ExitWithKeyboard(SimpleHome studio)
    {
        var keyHandler = studio.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
        var args = new object[] { Message.Create(studio.Handle, 0x104, IntPtr.Zero, IntPtr.Zero), Keys.Alt | Keys.F4 };
        Check((bool)keyHandler.Invoke(studio, args), "Studio did not handle the Alt+F4 exit action.");
    }

    private static void ExitFromTray(SimpleHome studio)
    {
        var menu = (ContextMenuStrip)ReadField(studio, "trayMenu");
        menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "退出软件").PerformClick();
    }

    private static async Task<Form> WaitForWindow(SimpleHome studio)
    {
        await WaitFor(() => GetOptimizerWindow(studio) != null, "Studio did not show its optimizer window.");
        return GetOptimizerWindow(studio);
    }

    private static Form GetOptimizerWindow(SimpleHome studio)
    {
        return studio.OwnedForms.SingleOrDefault(window => window.Controls.OfType<GameBoost.MainForm>().Any());
    }

    private static ProcessPriorityClass Priority(Process fixture)
    {
        fixture.Refresh();
        return fixture.PriorityClass;
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException error)
        {
            if ((error.HResult & 0xffff) == 32)
                return true;
            throw;
        }
    }

    private static bool VisibleNotificationIcon(Control control)
    {
        return control.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(NotifyIcon))
            .Select(field => field.GetValue(control) as NotifyIcon)
            .Any(icon => icon != null && icon.Visible);
    }

    private static object ReadField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field != null, "Missing Studio member: " + name);
        return field.GetValue(target);
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException(message);
            await Task.Delay(40);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
