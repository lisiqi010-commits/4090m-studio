using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RtxHub
{
    internal sealed class OptimizerWindow : Form
    {
        private readonly FileStream instanceLock;
        private readonly GameBoost.MainForm dashboard;
        private bool opening;

        internal OptimizerWindow(string folder)
        {
            Directory.CreateDirectory(folder);
            try
            {
                instanceLock = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                if ((error.HResult & 0xffff) == 32)
                    throw new InvalidOperationException("游戏优化助手已在运行。请先在它的托盘菜单选择「退出并恢复优先级」，再回到 Studio 打开游戏优化。", error);
                throw;
            }

            try
            {
                Text = "4090M Studio · 游戏优化";
                ClientSize = new Size(940, 740);
                MinimumSize = new Size(760, 615);
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                dashboard = new GameBoost.MainForm(folder, hosted: true);
                Controls.Add(dashboard);
                dashboard.Show();
                Resize += delegate
                {
                    if (!opening && WindowState == FormWindowState.Minimized && Visible)
                        Hide();
                };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void StartMonitoring()
        {
            dashboard.StartMonitoring();
        }

        internal Task<int> StopMonitoringAsync()
        {
            return dashboard.StopMonitoringAsync();
        }

        internal string OptimizationStatus { get { return dashboard.OptimizationStatus; } }
        internal bool OptimizationEnabled { get { return dashboard.OptimizationEnabled; } }

        internal void ToggleOptimization()
        {
            dashboard.ToggleOptimization();
        }

        internal void Open(Form owner)
        {
            opening = true;
            try
            {
                if (!Visible)
                    Show(owner);
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Activate();
            }
            finally
            {
                opening = false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            base.OnFormClosing(args);
            if (args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing && instanceLock != null)
                    instanceLock.Dispose();
            }
        }
    }
}
