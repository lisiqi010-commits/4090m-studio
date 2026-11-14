using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RtxHub;

public static class SimpleHomeTests
{
    static int checks;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    static List<T> Find<T>(Control parent) where T : Control
    {
        List<T> found = new List<T>();
        foreach (Control child in parent.Controls)
        {
            T match = child as T;
            if (match != null) found.Add(match);
            found.AddRange(Find<T>(child));
        }
        return found;
    }

    static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    [STAThread]
    public static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (SimpleHome form = new SimpleHome(true))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                Application.DoEvents();
                List<Button> buttons = Find<Button>(form);
                Check(buttons.Count == 3, "Home provides two media actions and the optimizer entry.");
                Check(buttons[0].Text == "选择游戏或视频", "First action label.");
                Check(buttons[1].Text == "取消全部注入", "Second action label.");
                Check(buttons[2].Name == "OpenGameOptimizer", "Optimizer entry is on the home screen.");
                foreach (Button button in buttons)
                {
                    Check(button.AccessibleName == button.Text, "Accessible action name.");
                    Check(!String.IsNullOrEmpty(button.AccessibleDescription), "Accessible action description.");
                    Check(button.TabStop, "Actions support keyboard navigation.");
                    Check(button.Enabled, "Actions ready initially.");
                }
                Check(Find<TabControl>(form).Count == 0, "No tabs.");
                Check(Find<CheckBox>(form).Count == 0, "No check boxes.");
                Check(Find<TextBox>(form).Count == 0, "No dense text boxes.");
                Check(form.AllowDrop, "Home accepts a dropped file.");
                Check(form.ClientSize.Width <= 900 && form.ClientSize.Height <= 650, "Small initial window.");
                Check(!((System.Windows.Forms.Timer)Field(form, "pendingTimer")).Enabled, "Render mode cannot poll backend.");
                foreach (Size size in new Size[] { form.ClientSize, new Size(660, 480), new Size(1024, 640) })
                {
                    form.ClientSize = size;
                    form.PerformLayout();
                    Application.DoEvents();
                    foreach (Button button in buttons)
                    {
                        Rectangle bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                        Check(form.ClientRectangle.Contains(bounds), "Card stays inside window at " + size);
                        if (button != buttons[2]) Check(button.Width >= 260 && button.Height >= 180, "Cards remain large at " + size);
                    }
                    Check(!buttons[0].Bounds.IntersectsWith(buttons[1].Bounds), "Action cards do not overlap.");
                    foreach (Label label in Find<Label>(form))
                        Check(label.Right <= label.Parent.ClientSize.Width + 1, "Labels fit their container at " + size);
                }
                bool foundFooter = false;
                foreach (Label label in Find<Label>(form))
                    if (label.Text == "选择即加载并启动；取消仅处理本工具管理的注入。") foundFooter = true;
                Check(foundFooter, "Ownership boundary remains visible.");
#if SIMPLE_HOME_TEST_STUB
                Check(SimpleActions.Calls == 0, "Construction/render cannot call backend.");
                Check(SimpleActions.PendingReads == 0, "Construction/render cannot inspect pending state.");
                buttons[1].PerformClick();
                Application.DoEvents();
                Check(SimpleActions.Calls == 0, "Preview does not execute cancellation.");
#endif
            }
            Check(SimpleHome.SupportsSelection("movie.MKV"), "Case insensitive video extension.");
            Check(SimpleHome.SupportsSelection("game.exe"), "Game selection.");
            Check(SimpleHome.SupportsSelection("movie.m2ts"), "Common transport stream selection.");
            Check(!SimpleHome.SupportsSelection("notes.txt"), "Unsupported extension refused.");
            Check(!SimpleHome.SupportsSelection(null), "Null selection refused.");
            Check(!SimpleHome.SupportsSelections(new string[0]), "Empty multi-selection refused.");
            Check(!SimpleHome.SupportsSelections(new[]{Assembly.GetExecutingAssembly().Location,Assembly.GetExecutingAssembly().Location}), "Two games cannot become a playlist.");
#if SIMPLE_HOME_TEST_STUB
            TestAsyncCancellation();
            TestPendingPolling();
#endif
            Console.WriteLine("SimpleHomeTests: " + checks + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

#if SIMPLE_HOME_TEST_STUB
    static void TestAsyncCancellation()
    {
        SimpleActions.Calls = 0;
        SimpleActions.Gate = new ManualResetEvent(false);
        using (SimpleHome form = new SimpleHome(false))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            List<Button> buttons = Find<Button>(form);
            buttons[1].PerformClick();
            Check(!buttons[0].Enabled && !buttons[1].Enabled, "Mutating operation disables both actions.");
            SimpleActions.Pending = true;
            Tick(form);
            Check(SimpleActions.PendingReads == 0, "Pending polling skips a busy form.");
            SimpleActions.Pending = false;
            form.Close();
            Check(!form.IsDisposed && form.Visible, "Cannot close during mutation.");
            buttons[1].PerformClick();
            SimpleActions.Gate.Set();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!buttons[0].Enabled && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(buttons[0].Enabled && buttons[1].Enabled, "Actions enabled after backend returns.");
            Check(SimpleActions.Calls == 1, "Concurrent cancellation is prevented.");
            Check(((Label)Field(form, "status")).Text == "已取消本工具管理的注入。", "Backend outcome visible.");
        }
        SimpleActions.Gate.Dispose();
        SimpleActions.Gate = null;
    }

    static void Tick(SimpleHome form)
    {
        System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)Field(form, "pendingTimer");
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(timer, new object[] { EventArgs.Empty });
    }

    static void WaitReady(SimpleHome form)
    {
        List<Button> buttons = Find<Button>(form);
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!buttons[0].Enabled && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Check(buttons[0].Enabled, "Asynchronous action completed.");
    }

    static void TestPendingPolling()
    {
        SimpleActions.Calls = 0;
        SimpleActions.Pending = false;
        using (SimpleHome form = new SimpleHome(false))
        {
            form.ShowInTaskbar = true;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)Field(form, "pendingTimer");
            Check(timer.Enabled && timer.Interval == 3000, "Normal mode polls every three seconds.");
            Tick(form);
            Check(SimpleActions.Calls == 0, "No backend polling without pending cancellation.");
            Label status = (Label)Field(form, "status");
            int changes = 0;
            status.TextChanged += delegate { changes++; };
            SimpleActions.Pending = true;
            Tick(form);
            WaitReady(form);
            Tick(form);
            WaitReady(form);
            Check(SimpleActions.Calls == 2, "Pending cancellations are retried.");
            Check(changes == 0, "Unchanged pending state stays quiet.");
            SimpleActions.Pending = false;

            string ownExecutable = Assembly.GetExecutingAssembly().Location;
            form.GetType().GetMethod("OpenSelection", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, new object[] { ownExecutable });
            WaitReady(form);
            Check(SimpleActions.Selection == ownExecutable, "Selected file reaches backend unchanged.");
            Check(((Label)Field(form, "selected")).Text.EndsWith("SimpleHomeTests.exe"), "Selected filename remains visible.");
            Check(status.Text == "已启动。", "Launch outcome is shown without keeping form busy.");
            IntPtr originalHandle = form.Handle;
            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            NotifyIcon tray = (NotifyIcon)Field(form, "trayIcon");
            Check(tray.Visible && !form.Visible, "Minimize hides to a visible tray icon.");
            Check(form.Handle == originalHandle, "Hiding to tray preserves the native window handle.");
            Check(((System.Windows.Forms.Timer)Field(form, "pendingTimer")).Enabled, "Tray mode keeps pending cancellation active.");
            ContextMenuStrip menu = (ContextMenuStrip)Field(form, "trayMenu");
            Check(menu.Items[0].Text == "打开主窗口" && menu.Items[1].Text == "游戏优化" && menu.Items[2].Text == "退出软件", "Chinese tray menu provides restore, optimizer and exit.");
            menu.Items[0].PerformClick();
            Application.DoEvents();
            Check(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Normal && !tray.Visible, "Tray open restores the window and removes hidden icon.");
            form.WindowState=FormWindowState.Minimized;Application.DoEvents();
            typeof(NotifyIcon).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(tray,new object[]{new MouseEventArgs(MouseButtons.Right,1,0,0,0)});
            Check(!form.Visible&&tray.Visible,"Right click keeps the window hidden for the context menu.");
            typeof(NotifyIcon).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(tray,new object[]{new MouseEventArgs(MouseButtons.Left,1,0,0,0)});
            Application.DoEvents();
            Check(form.Visible&&!tray.Visible&&form.Handle==originalHandle,"A single left click restores the window with its original handle.");
            Check(Screen.FromControl(form).WorkingArea.IntersectsWith(form.Bounds), "Restored window is on screen, not at Windows minimized parking coordinates.");
            for(int cycle=0;cycle<3;cycle++)
            {
                form.WindowState=FormWindowState.Minimized;Application.DoEvents();
                menu.Items[0].PerformClick();Application.DoEvents();
                Check(form.Visible&&form.ShowInTaskbar&&!tray.Visible&&Screen.FromControl(form).WorkingArea.IntersectsWith(form.Bounds),"Repeated tray restoration remains visible.");
                Check(form.Handle==originalHandle,"Repeated tray cycles keep the notification owner's handle stable.");
            }
            form.Close();Application.DoEvents();
            Check(!form.IsDisposed&&!form.Visible&&tray.Visible,"Title-bar close hides to tray without exiting.");
            menu.Items[0].PerformClick();Application.DoEvents();
            object[] keyArgs={Message.Create(form.Handle,0x104,IntPtr.Zero,IntPtr.Zero),Keys.Alt|Keys.F4};
            form.GetType().GetMethod("ProcessCmdKey",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,keyArgs);
            Check(form.IsDisposed,"Alt+F4 explicitly exits the application.");
        }
        using(var startup=new SimpleHome(false,true))
        {
            startup.Show();Application.DoEvents();
            Check(!startup.Visible&&((NotifyIcon)Field(startup,"trayIcon")).Visible,"Startup launch hides in tray.");
            ((ContextMenuStrip)Field(startup,"trayMenu")).Items[2].PerformClick();
            Check(startup.IsDisposed,"Tray exit explicitly closes even while hidden.");
        }
    }
#endif
}

#if SIMPLE_HOME_TEST_STUB
namespace RtxHub
{
    public enum SourceKind { Files, PotPlayer, Browser }
    public sealed class SourceSelection { public SourceKind Kind;public string[] Files;public IntPtr BrowserHandle; }
    public static class SourcePicker { public static SourceSelection Select(IWin32Window owner) { return null; } }
    public static class GameLaunchOptions { public static int? Select(IWin32Window owner,string exe) { return 1; } }
    // Compile only this form and these tests with SIMPLE_HOME_TEST_STUB.
    // No executable, DLL, file, registry or network operation occurs here.
    public static class SimpleActions
    {
        public static int Calls;
        public static int PendingReads;
        public static bool Pending;
        public static string Selection;
        public static ManualResetEvent Gate;
        public static bool HasPendingCancellation { get { Interlocked.Increment(ref PendingReads); return Pending; } }
        public static string Open(string selection, Action<string> progress)
        {
            Interlocked.Increment(ref Calls);
            Selection = selection;
            return "已启动。";
        }
        public static string OpenMany(string[] selections, Action<string> progress) { return Open(selections[0],progress); }
        public static string OpenMany(string[] selections,int multiplier,Action<string> progress) { return Open(selections[0],progress); }
        public static string OpenPotPlayer(Action<string> progress) { return Open("PotPlayer",progress); }
        public static string OpenBrowser(IntPtr handle,Action<string> progress) { return Open("browser",progress); }
        public static string CancelAll(Action<string> progress)
        {
            Interlocked.Increment(ref Calls);
            progress("正在取消本工具管理的注入…");
            if (Gate != null) Gate.WaitOne();
            return "已取消本工具管理的注入。";
        }
        public static string PollPending(Action<string> progress)
        {
            Interlocked.Increment(ref Calls);
            return "";
        }
    }
}
#endif
