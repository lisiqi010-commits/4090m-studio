using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GameBoost
{
    internal sealed class MainForm : Form
    {
        private enum AppSortColumn { Name = 1, Processes, Cpu, Memory, Status }

        private static readonly AppSortColumn[] SortChoices =
            { AppSortColumn.Cpu, AppSortColumn.Memory, AppSortColumn.Name, AppSortColumn.Processes, AppSortColumn.Status };

        private static readonly Color Ink = Color.FromArgb(24, 37, 61);
        private static readonly Color Muted = Color.FromArgb(77, 95, 119);
        private static readonly Color Surface = Color.FromArgb(244, 247, 251);
        private static readonly Color Accent = Color.FromArgb(37, 88, 221);
        private static readonly Color Navy = Color.FromArgb(22, 39, 72);
        private static readonly Color Positive = Color.FromArgb(160, 246, 198);
        private static readonly Color Warning = Color.FromArgb(255, 218, 157);

        private readonly Settings settings;
        private readonly GameProfiles gameProfiles;
        private readonly OptimizationEngine engine;
        private readonly SystemUsageMonitor usageMonitor = new SystemUsageMonitor();
        private readonly Timer timer = new Timer { Interval = 4000 };
        private readonly Timer usageTimer = new Timer { Interval = 1000 };
        private readonly DataGridView apps = new DataGridView { Name = "AppList" };
        private readonly Label status = new Label { Name = "OptimizationStatus" };
        private readonly Label summary = new Label();
        private readonly Button toggle = new Button { Name = "ToggleOptimization" };
        private readonly CheckBox defaults = new CheckBox();
        private readonly ResourceTile cpuUsage = new ResourceTile("CPU", Accent) { Name = "CpuUsage" };
        private readonly ResourceTile memoryUsage = new ResourceTile("内存", Color.FromArgb(10, 122, 105)) { Name = "MemoryUsage" };
        private readonly ResourceTile gpuUsage = new ResourceTile("GPU", Color.FromArgb(109, 65, 185)) { Name = "GpuUsage" };
        private readonly TextBox search = new TextBox { Name = "SearchApps", AccessibleName = "按程序名称或路径搜索" };
        private readonly ComboBox sort = new ComboBox { Name = "SortApps", AccessibleName = "后台应用排序", DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox selectedOnly = new CheckBox { Name = "SelectedOnly", Text = "仅显示已勾选", AutoSize = true };
        private readonly Button bulkSelect = new Button { Name = "BulkSelectApps", AccessibleName = "全选当前筛选结果" };
        private readonly Button bulkClear = new Button { Name = "BulkClearApps", AccessibleName = "取消全选当前筛选结果" };
        private readonly Label listSummary = new Label();
        private readonly Label modeLabel = new Label();
        private readonly ComboBox profilePicker = new ComboBox
        {
            Name = "GameProfilePicker", AccessibleName = "编辑优化配置", DropDownStyle = ComboBoxStyle.DropDownList
        };
        private readonly Button gameProfilesButton = new Button { Name = "GameProfilesButton" };
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripLabel trayStatus;
        private readonly ToolStripMenuItem trayOpen;
        private readonly ToolStripMenuItem trayToggle;
        private readonly ToolStripMenuItem trayExit;

        private Task refreshTask = Task.FromResult(0);
        private Task<int> stopTask;
        private Task<SystemUsageSnapshot> usageWork;
        private OptimizationSnapshot lastSnapshot = new OptimizationSnapshot();
        private GameProfile editedProfile;
        private FormWindowState restoreWindowState = FormWindowState.Normal;
        private AppSortColumn sortColumn = AppSortColumn.Cpu;
        private bool sortDescending = true;
        private bool active = true;
        private bool recover = true;
        private bool refreshRequested;
        private bool rendering;
        private bool changingProfile;
        private bool restoringWindow;
        private bool monitoring;
        private bool closing;
        private bool allowClose;

        internal string OptimizationStatus { get; private set; }
        internal bool OptimizationEnabled { get { return active; } }

        internal MainForm(string folder, bool hosted = false)
        {
            engine = new OptimizationEngine(Path.Combine(folder, "priority-journal.txt"));
            try
            {
                settings = new Settings(folder);
                gameProfiles = new GameProfiles(folder);
            }
            catch (Exception settingsError)
            {
                try
                {
                    engine.Restore();
                }
                catch (Exception restoreError)
                {
                    throw new InvalidOperationException(settingsError.Message + Environment.NewLine + restoreError.Message, settingsError);
                }
                throw;
            }

            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F);
            Text = "游戏优化助手";
            ClientSize = new Size(940, 740);
            BackColor = Surface;
            ForeColor = Ink;
            if (hosted)
            {
                TopLevel = false;
                FormBorderStyle = FormBorderStyle.None;
                Dock = DockStyle.Fill;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(760, 615);
                trayIcon = new NotifyIcon();
                trayStatus = new ToolStripLabel("正在读取优化状态…");
                trayOpen = new ToolStripMenuItem("打开窗口");
                trayToggle = new ToolStripMenuItem("暂停并恢复优先级");
                trayExit = new ToolStripMenuItem("退出并恢复优先级");
                ConfigureTray();
                Shown += delegate
                {
                    trayIcon.Visible = true;
                    StartMonitoring();
                };
                FormClosing += OnClosing;
                Resize += OnWindowResize;
            }

            BuildLayout();
            UpdateProfilePicker(null, gameProfiles.AutoMode);
            SetStatus("正在启动优化…", "正在读取后台应用", Positive);
            cpuUsage.UpdateValue(null, "正在采样…");
            memoryUsage.UpdateValue(null, "正在采样…");
            gpuUsage.UpdateValue(null, "正在采样…");

            timer.Tick += delegate { RequestRefresh(); };
            usageTimer.Tick += delegate { SampleUsage(); };
            Disposed += delegate
            {
                monitoring = false;
                timer.Dispose();
                usageTimer.Dispose();
                if (usageWork != null && !usageWork.IsCompleted)
                    usageWork.ContinueWith(task => usageMonitor.Dispose(), TaskScheduler.Default);
                else
                    usageMonitor.Dispose();
            };
        }

        internal void StartMonitoring()
        {
            if (monitoring || closing || IsDisposed || (stopTask != null && !stopTask.IsCompleted))
                return;

            monitoring = true;
            recover = true;
            toggle.Enabled = true;
            timer.Start();
            usageTimer.Start();
            RequestRefresh();
            SampleUsage();
        }

        internal Task<int> StopMonitoringAsync()
        {
            if (stopTask != null && !stopTask.IsCompleted)
                return stopTask;
            if (IsDisposed)
                throw new ObjectDisposedException("MainForm");

            monitoring = false;
            refreshRequested = false;
            timer.Stop();
            usageTimer.Stop();
            toggle.Enabled = false;
            SetStatus("正在恢复优先级…", String.Empty, Positive);
            stopTask = RestoreAfterRefreshAsync();
            return stopTask;
        }

        private async Task<int> RestoreAfterRefreshAsync()
        {
            // Let a host FormClosing event return before it can retry the close.
            await Task.Yield();
            Exception refreshError = null;
            try
            {
                await refreshTask;
            }
            catch (Exception error)
            {
                refreshError = error;
            }

            int remaining = await Task.Run(() => engine.Restore());
            if (refreshError != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(refreshError).Throw();

            if (!IsDisposed)
            {
                string details = remaining == 0
                    ? "已恢复本次调整的优先级"
                    : String.Format(CultureInfo.CurrentCulture, "仍有 {0} 个进程待恢复", remaining);
                SetStatus("优化已停止", details, remaining == 0 ? Positive : Warning);
            }
            return remaining;
        }

        private void ConfigureTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { trayStatus, new ToolStripSeparator(), trayOpen, trayToggle, new ToolStripSeparator(), trayExit });
            menu.DefaultDropDownDirection = ToolStripDropDownDirection.AboveRight;
            trayIcon.Icon = Icon ?? SystemIcons.Application;
            trayIcon.Text = Text;
            trayIcon.ContextMenuStrip = menu;
            trayIcon.MouseDoubleClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                    RestoreWindow();
            };
            trayOpen.Click += delegate { RestoreWindow(); };
            trayToggle.Click += delegate { ToggleOptimization(); };
            trayExit.Click += delegate { ExitApplication(); };
            Disposed += delegate
            {
                trayIcon.Dispose();
                menu.Dispose();
            };
        }

        private void OnWindowResize(object sender, EventArgs args)
        {
            if (restoringWindow)
                return;

            if (WindowState != FormWindowState.Minimized)
                restoreWindowState = WindowState;
            else if (!closing && Visible && trayIcon.Visible)
                Hide();
        }

        private void RestoreWindow()
        {
            if (closing)
                return;

            restoringWindow = true;
            try
            {
                // Showing first lets Windows apply its saved placement before we restore the window state.
                Show();
                WindowState = restoreWindowState;
                Activate();
            }
            finally
            {
                restoringWindow = false;
            }
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(22, 20, 22, 12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            Controls.Add(layout);

            var hero = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Navy,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(20, 12, 20, 10)
            };
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 133));
            hero.RowStyles.Add(new RowStyle(SizeType.Percent, 49));
            hero.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
            hero.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
            hero.Controls.Add(MakeLabel("游戏优化助手", Color.White, 22F, FontStyle.Bold), 0, 0);
            StyleLabel(status, Positive, 11F, FontStyle.Bold);
            StyleLabel(summary, Color.FromArgb(213, 225, 247), 9.5F, FontStyle.Regular);
            hero.Controls.Add(status, 0, 1);
            hero.Controls.Add(summary, 0, 2);
            StyleButton(toggle, "暂停并恢复", true);
            toggle.Anchor = AnchorStyles.Right;
            toggle.Size = new Size(130, 42);
            toggle.Click += delegate { ToggleOptimization(); };
            hero.Controls.Add(toggle, 1, 0);
            hero.SetRowSpan(toggle, 3);
            layout.Controls.Add(hero, 0, 0);
            layout.Controls.Add(BuildUsagePanel(), 0, 1);
            layout.Controls.Add(BuildProfileBar(), 0, 2);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.Controls.Add(MakeLabel("后台应用", Ink, 14F, FontStyle.Bold), 0, 0);

            defaults.Text = "自动优化同步软件";
            defaults.AccessibleDescription = "自动降低夸克网盘、OneDrive、Dropbox 和 Google Drive 的 CPU 优先级";
            defaults.AutoSize = true;
            defaults.Anchor = AnchorStyles.Right;
            defaults.ForeColor = Ink;
            defaults.Margin = new Padding(0, 0, 18, 0);
            defaults.Checked = settings.IncludeDefaults;
            defaults.CheckedChanged += delegate { ChangeSyncDefaults(); };
            toolbar.Controls.Add(defaults, 1, 0);

            var add = new Button { Anchor = AnchorStyles.Right, Size = new Size(116, 36) };
            StyleButton(add, "添加程序…", false);
            add.Click += delegate { AddExecutable(); };
            toolbar.Controls.Add(add, 2, 0);
            layout.Controls.Add(toolbar, 0, 3);
            layout.Controls.Add(BuildFilters(), 0, 4);

            ConfigureGrid();
            layout.Controls.Add(apps, 0, 5);
            StyleLabel(listSummary, Muted, 9F, FontStyle.Regular);
            listSummary.Text = "正在读取应用…";
            layout.Controls.Add(listSummary, 0, 6);
        }

        private TableLayoutPanel BuildProfileBar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 9, 0, 0)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 97));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            bar.Controls.Add(MakeLabel("编辑配置", Ink, 10F, FontStyle.Bold), 0, 0);
            profilePicker.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            profilePicker.Margin = new Padding(0, 0, 14, 0);
            profilePicker.SelectedIndexChanged += delegate
            {
                if (!changingProfile && profilePicker.SelectedIndex >= 0)
                    SelectProfile(profilePicker.SelectedIndex);
            };
            bar.Controls.Add(profilePicker, 1, 0);
            StyleLabel(modeLabel, Muted, 9F, FontStyle.Regular);
            bar.Controls.Add(modeLabel, 2, 0);
            StyleButton(gameProfilesButton, "游戏配置…", false);
            gameProfilesButton.Anchor = AnchorStyles.Right;
            gameProfilesButton.Size = new Size(116, 32);
            gameProfilesButton.Click += delegate { ManageGameProfiles(); };
            bar.Controls.Add(gameProfilesButton, 3, 0);
            return bar;
        }

        private void ManageGameProfiles()
        {
            string selectedPath = editedProfile == null ? null : editedProfile.GamePath;
            bool wasAuto = gameProfiles.AutoMode;
            if (trayExit != null)
                trayExit.Enabled = false;
            try
            {
                using (var dialog = new GameProfilesDialog(gameProfiles, settings))
                {
                    if (dialog.ShowDialog(TopLevelControl ?? this) != DialogResult.OK)
                        return;
                }
            }
            finally
            {
                if (trayExit != null)
                    trayExit.Enabled = !closing;
            }

            UpdateProfilePicker(selectedPath, gameProfiles.AutoMode && !wasAuto);
            RequestRefresh();
        }

        private void UpdateProfilePicker(string selectedPath, bool selectFirstGame)
        {
            changingProfile = true;
            try
            {
                profilePicker.Items.Clear();
                profilePicker.Items.Add("手动优化名单");
                foreach (GameProfile profile in gameProfiles.Profiles)
                    profilePicker.Items.Add(Path.GetFileNameWithoutExtension(profile.GamePath));

                int index = selectedPath == null ? -1 : gameProfiles.Profiles.FindIndex(profile => SamePath(profile.GamePath, selectedPath));
                if (index == -1 && selectFirstGame && gameProfiles.Profiles.Count > 0)
                    index = 0;
                profilePicker.SelectedIndex = index + 1;
                SelectProfile(index + 1);
                modeLabel.Text = gameProfiles.AutoMode ? "运行模式：按游戏自动优化" : "运行模式：手动名单";
            }
            finally
            {
                changingProfile = false;
            }
        }

        private void SelectProfile(int index)
        {
            bool wasChanging = changingProfile;
            changingProfile = true;
            try
            {
                editedProfile = index == 0 ? null : gameProfiles.Profiles[index - 1];
                defaults.Checked = EditIncludesDefaults;
            }
            finally
            {
                changingProfile = wasChanging;
            }
            RenderApps(false);
        }

        internal void ToggleOptimization()
        {
            if (closing)
                return;

            active = !active;
            toggle.Text = active ? "暂停并恢复" : "启动优化";
            if (trayToggle != null)
                trayToggle.Text = active ? "暂停并恢复优先级" : "恢复优化";
            SetStatus(active ? "正在启动优化…" : "正在恢复优先级…", String.Empty, Positive);
            RequestRefresh();
        }

        private TableLayoutPanel BuildUsagePanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 12, 0, 0)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ResourceTile[] tiles = { cpuUsage, memoryUsage, gpuUsage };
            for (int index = 0; index < tiles.Length; index++)
            {
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / tiles.Length));
                tiles[index].Dock = DockStyle.Fill;
                tiles[index].Margin = new Padding(0, 0, index == tiles.Length - 1 ? 0 : 10, 0);
                panel.Controls.Add(tiles[index], index, 0);
            }
            return panel;
        }

        private TableLayoutPanel BuildFilters()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(MakeLabel("搜索", Muted, 10F, FontStyle.Regular), 0, 0);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            search.Margin = new Padding(0, 0, 18, 0);
            search.TextChanged += delegate { RenderApps(false); };
            panel.Controls.Add(search, 1, 0);

            panel.Controls.Add(MakeLabel("排序", Muted, 10F, FontStyle.Regular), 2, 0);
            sort.Items.AddRange(new object[] { "CPU", "内存", "名称", "进程数", "状态" });
            sort.SelectedIndex = 0;
            sort.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            sort.Margin = new Padding(0, 0, 12, 0);
            sort.SelectedIndexChanged += delegate
            {
                if (sort.SelectedIndex < 0)
                    return;
                AppSortColumn selected = SortChoices[sort.SelectedIndex];
                if (selected != sortColumn)
                    SetAppSort(selected, DefaultDescending(selected));
            };
            panel.Controls.Add(sort, 3, 0);

            selectedOnly.Anchor = AnchorStyles.Left;
            selectedOnly.Margin = new Padding(3, 0, 0, 0);
            selectedOnly.CheckedChanged += delegate { RenderApps(false); };
            panel.Controls.Add(selectedOnly, 4, 0);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(14, 0, 0, 0)
            };
            StyleButton(bulkSelect, "全选", false);
            StyleButton(bulkClear, "取消全选", false);
            bulkSelect.Size = new Size(56, 30);
            bulkClear.Size = new Size(86, 30);
            bulkClear.Margin = new Padding(6, 0, 0, 0);
            bulkSelect.Enabled = bulkClear.Enabled = false;
            bulkSelect.Click += delegate { SelectVisibleApps(true); };
            bulkClear.Click += delegate { SelectVisibleApps(false); };
            buttons.Controls.AddRange(new Control[] { bulkSelect, bulkClear });
            panel.Controls.Add(buttons, 5, 0);
            return panel;
        }

        private void ConfigureGrid()
        {
            apps.Dock = DockStyle.Fill;
            apps.Margin = new Padding(0);
            apps.AllowUserToAddRows = false;
            apps.AllowUserToDeleteRows = false;
            apps.AllowUserToResizeRows = false;
            apps.AllowUserToOrderColumns = false;
            apps.RowHeadersVisible = false;
            apps.EnableHeadersVisualStyles = false;
            apps.MultiSelect = false;
            apps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            apps.BackgroundColor = Color.White;
            apps.BorderStyle = BorderStyle.FixedSingle;
            apps.GridColor = Color.FromArgb(224, 230, 238);
            apps.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            apps.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            apps.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            apps.ColumnHeadersHeight = 39;
            apps.RowTemplate.Height = 36;
            apps.DefaultCellStyle.ForeColor = Ink;
            apps.DefaultCellStyle.SelectionBackColor = Color.FromArgb(228, 237, 255);
            apps.DefaultCellStyle.SelectionForeColor = Ink;
            apps.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
            apps.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
            apps.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 239, 250);
            apps.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            apps.ColumnHeadersDefaultCellStyle.SelectionBackColor = apps.ColumnHeadersDefaultCellStyle.BackColor;
            apps.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "优化", Width = 62, SortMode = DataGridViewColumnSortMode.NotSortable });
            apps.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "程序", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 145, SortMode = DataGridViewColumnSortMode.Programmatic
            });
            AddColumn("进程数", 77);
            AddColumn("CPU", 85);
            AddColumn("内存", 101);
            AddColumn("状态", 128);
            apps.Columns[(int)sortColumn].HeaderCell.SortGlyphDirection = SortOrder.Descending;
            apps.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs args)
            {
                if (args.Button != MouseButtons.Left || args.ColumnIndex < 1)
                    return;

                var selected = (AppSortColumn)args.ColumnIndex;
                SetAppSort(selected, selected == sortColumn ? !sortDescending : DefaultDescending(selected));
            };
            apps.CurrentCellDirtyStateChanged += delegate
            {
                if (apps.IsCurrentCellDirty && apps.CurrentCell is DataGridViewCheckBoxCell)
                    apps.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            apps.CellValueChanged += OnSelectionChanged;
            var menu = new ContextMenuStrip();
            var openFolder = new ToolStripMenuItem("打开程序所在文件夹");
            var copyPath = new ToolStripMenuItem("复制程序路径");
            menu.Items.AddRange(new ToolStripItem[] { openFolder, copyPath });
            string menuPath = null;
            menu.Opening += delegate(object sender, CancelEventArgs args)
            {
                menuPath = CurrentAppPath();
                args.Cancel = menuPath == null;
                openFolder.Enabled = GetProgramFolder(menuPath) != null;
            };
            openFolder.Click += delegate { OpenProgramFolder(menuPath); };
            copyPath.Click += delegate
            {
                if (menuPath == null)
                    return;

                try
                {
                    Clipboard.SetText(menuPath);
                }
                catch (ExternalException)
                {
                    MessageBox.Show(this, "剪贴板暂时不可用，请稍后重试。", Text);
                }
            };
            apps.ContextMenuStrip = menu;
            apps.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Right)
                    return;
                int row = apps.HitTest(args.X, args.Y).RowIndex;
                apps.CurrentCell = row >= 0 ? apps.Rows[row].Cells[(int)AppSortColumn.Name] : null;
            };
            Disposed += delegate { menu.Dispose(); };
        }

        private void AddColumn(string title, int width)
        {
            apps.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic
            });
        }

        private void SetAppSort(AppSortColumn column, bool descending)
        {
            apps.Columns[(int)sortColumn].HeaderCell.SortGlyphDirection = SortOrder.None;
            sortColumn = column;
            sortDescending = descending;
            sort.SelectedIndex = Array.IndexOf(SortChoices, column);
            apps.Columns[(int)column].HeaderCell.SortGlyphDirection = descending ? SortOrder.Descending : SortOrder.Ascending;
            RenderApps(false);
        }

        private static bool DefaultDescending(AppSortColumn column)
        {
            return column == AppSortColumn.Cpu || column == AppSortColumn.Memory || column == AppSortColumn.Processes;
        }

        private static string GetProgramFolder(string path)
        {
            string folder = Path.GetDirectoryName(path);
            return Directory.Exists(folder) ? folder : null;
        }

        private string CurrentAppPath()
        {
            return apps.CurrentRow == null ? null : ((AppUsage)apps.CurrentRow.Tag).Path;
        }

        private void OpenProgramFolder(string path)
        {
            string folder = GetProgramFolder(path);
            if (folder == null)
            {
                MessageBox.Show(this, "程序所在文件夹已不存在。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" })) { }
            }
            catch (Win32Exception error)
            {
                MessageBox.Show(this, "无法打开程序所在文件夹。\n\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnSelectionChanged(object sender, DataGridViewCellEventArgs args)
        {
            if (rendering || args.ColumnIndex != 0 || args.RowIndex < 0)
                return;

            DataGridViewRow row = apps.Rows[args.RowIndex];
            var app = (AppUsage)row.Tag;
            if (!IsProtectedInEditor(app.Path))
                SaveAppSelection(new[] { app }, (bool)row.Cells[0].Value);
        }

        private void SelectVisibleApps(bool selected)
        {
            AppUsage[] changed = apps.Rows.Cast<DataGridViewRow>().Select(row => (AppUsage)row.Tag)
                .Where(app => !IsProtectedInEditor(app.Path) && IsSelected(app) != selected).ToArray();
            if (changed.Length != 0)
                SaveAppSelection(changed, selected);
        }

        private void SaveAppSelection(AppUsage[] entries, bool selected)
        {
            HashSet<string> paths = EditPaths;
            HashSet<string> exclusions = EditExclusions;
            string[] previousPaths = paths.ToArray();
            string[] previousExclusions = exclusions.ToArray();
            foreach (AppUsage app in entries)
                SetAppSelection(app, selected);

            try
            {
                SaveCurrentConfiguration();
            }
            catch (Exception error)
            {
                paths.Clear();
                paths.UnionWith(previousPaths);
                exclusions.Clear();
                exclusions.UnionWith(previousExclusions);
                ShowSettingsError(error);
            }
            RequestRefresh();
        }

        private void SetAppSelection(AppUsage app, bool selected)
        {
            string path = app.Path;
            bool fromDefault = EditIncludesDefaults && app.IsDefault;
            if (selected)
            {
                if (fromDefault)
                    EditExclusions.Remove(path);
                else
                    EditPaths.Add(path);
            }
            else
            {
                EditPaths.Remove(path);
                if (fromDefault)
                    EditExclusions.Add(path);
            }
        }

        private HashSet<string> EditPaths { get { return editedProfile == null ? settings.Paths : editedProfile.Paths; } }
        private HashSet<string> EditExclusions { get { return editedProfile == null ? settings.ExcludedDefaults : editedProfile.ExcludedDefaults; } }

        private bool EditIncludesDefaults
        {
            get { return editedProfile == null ? settings.IncludeDefaults : editedProfile.IncludeDefaults; }
            set
            {
                if (editedProfile == null)
                    settings.IncludeDefaults = value;
                else
                    editedProfile.IncludeDefaults = value;
            }
        }

        private void ChangeSyncDefaults()
        {
            if (changingProfile || rendering)
                return;

            bool previous = EditIncludesDefaults;
            EditIncludesDefaults = defaults.Checked;
            if (!SaveSettings())
            {
                EditIncludesDefaults = previous;
                changingProfile = true;
                defaults.Checked = previous;
                changingProfile = false;
            }
            RequestRefresh();
        }

        private void SaveCurrentConfiguration()
        {
            if (editedProfile == null)
                settings.Save();
            else
                gameProfiles.Save();
        }

        private void AddExecutable()
        {
            using (var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true, Title = "选择要优化的后台程序" })
            {
                if (dialog.ShowDialog(TopLevelControl ?? this) != DialogResult.OK)
                    return;

                string path = Path.GetFullPath(dialog.FileName);
                if (IsProtectedInEditor(path))
                {
                    MessageBox.Show(this, "此程序已受保护。可在游戏配置中管理保护名单。", Text);
                    return;
                }
                if (EditPaths.Add(path) && !SaveSettings())
                    EditPaths.Remove(path);
                RequestRefresh();
            }
        }

        private bool SaveSettings()
        {
            try
            {
                SaveCurrentConfiguration();
                return true;
            }
            catch (Exception error)
            {
                ShowSettingsError(error);
                return false;
            }
        }

        private void ShowSettingsError(Exception error)
        {
            MessageBox.Show(this, "保存设置失败。\n\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void RequestRefresh()
        {
            if (!monitoring || closing)
                return;

            refreshRequested = true;
            if (refreshTask.IsCompleted)
                refreshTask = RefreshAsync();
        }

        private async void SampleUsage()
        {
            if (!monitoring || closing || (usageWork != null && !usageWork.IsCompleted))
                return;

            usageWork = Task.Run(() => usageMonitor.Read());
            SystemUsageSnapshot sample;
            try
            {
                sample = await usageWork;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("System resource sampling failed: {0}", error.Message);
                if (monitoring && !closing && !IsDisposed)
                {
                    cpuUsage.UpdateValue(null, "暂时无法读取");
                    memoryUsage.UpdateValue(null, "暂时无法读取");
                    gpuUsage.UpdateValue(null, "暂时无法读取");
                }
                return;
            }

            if (!monitoring || closing || IsDisposed)
                return;

            cpuUsage.UpdateValue(sample.CpuPercent, sample.CpuPercent.HasValue ? "整机占用" : "等待采样");
            if (sample.MemoryTotalBytes.GetValueOrDefault() > 0 && sample.MemoryUsedBytes.HasValue)
            {
                double percent = 100.0 * sample.MemoryUsedBytes.Value / sample.MemoryTotalBytes.Value;
                string detail = String.Format(CultureInfo.CurrentCulture, "{0:0.0} / {1:0.0} GB",
                    sample.MemoryUsedBytes.Value / BytesPerGigabyte, sample.MemoryTotalBytes.Value / BytesPerGigabyte);
                memoryUsage.UpdateValue(percent, detail);
            }
            else
                memoryUsage.UpdateValue(null, "暂时无法读取");

            gpuUsage.UpdateValue(sample.GpuPercent, sample.GpuPercent.HasValue ? "最忙引擎" : "暂无计数器数据");
        }

        private async Task RefreshAsync()
        {
            // Let grid events finish before a refresh can rebuild their rows.
            await Task.Yield();
            while (refreshRequested && monitoring && !closing)
            {
                refreshRequested = false;
                bool optimize = active;
                bool useDefaults = settings.IncludeDefaults;
                bool restoreFirst = recover;
                string[] selection = settings.Paths.ToArray();
                string[] excluded = settings.ExcludedDefaults.ToArray();
                bool autoMode = gameProfiles.AutoMode;
                GameProfile[] profiles = autoMode ? gameProfiles.Profiles.Select(profile => profile.Clone()).ToArray() : null;
                string[] games = autoMode ? null : gameProfiles.Profiles.Select(profile => profile.GamePath).ToArray();

                try
                {
                    OptimizationSnapshot snapshot = await Task.Run(delegate
                    {
                        if (restoreFirst)
                            engine.Restore();
                        return autoMode ? engine.ScanGames(profiles, optimize) : engine.Scan(selection, useDefaults, optimize, excluded, games);
                    });

                    recover = false;
                    if (!monitoring || closing || optimize != active || refreshRequested)
                        continue;

                    lastSnapshot = snapshot;
                    RenderApps(true);
                    UpdateOptimizationStatus(snapshot, optimize, autoMode);
                }
                catch (Exception error)
                {
                    if (monitoring && !closing)
                        SetStatus("暂时无法更新优化状态", error.Message, Warning);
                }
            }
        }

        private void UpdateOptimizationStatus(OptimizationSnapshot snapshot, bool enabled, bool autoMode)
        {
            string title = !enabled ? "优化已暂停" : snapshot.IsOptimizationActive ? "优化运行中" : "等待游戏启动";
            string gameName = snapshot.ActiveGamePath == null ? null : Path.GetFileNameWithoutExtension(snapshot.ActiveGamePath);
            var details = new List<string>();
            if (gameName != null)
                details.Add("游戏：" + gameName);
            if (snapshot.IsOptimizationActive)
                details.Add(String.Format(CultureInfo.CurrentCulture, "已降低 {0} 个进程的 CPU 优先级", snapshot.AdjustedCount));
            else if (autoMode && gameProfiles.Profiles.Count == 0 && enabled)
                details.Add("请先在「游戏配置」中添加游戏");
            else if (snapshot.PendingRestoreCount == 0)
                details.Add("已恢复本次调整的优先级");
            if (snapshot.SkippedCount > 0)
                details.Add(String.Format(CultureInfo.CurrentCulture, "{0} 个进程未能调整", snapshot.SkippedCount));
            if (snapshot.PendingRestoreCount > 0)
                details.Add(String.Format(CultureInfo.CurrentCulture, "{0} 个进程待恢复", snapshot.PendingRestoreCount));
            bool warning = snapshot.SkippedCount > 0 || snapshot.PendingRestoreCount > 0;
            SetStatus(title, String.Join("   ·   ", details), warning ? Warning : Positive);
            string trayText = gameName == null ? title : title + " · " + gameName;
            if (snapshot.IsOptimizationActive)
                trayText += " · 已调整 " + snapshot.AdjustedCount.ToString(CultureInfo.CurrentCulture) + " 个";
            OptimizationStatus = trayText;
            if (trayToggle == null)
                return;

            trayToggle.ToolTipText = trayText;
            trayStatus.Text = trayText.Length <= 52 ? trayText : trayText.Substring(0, 51) + "…";
            trayText = "游戏优化助手 · " + trayText;
            trayIcon.Text = trayText.Length <= 63 ? trayText : trayText.Substring(0, 62) + "…";
        }

        private void RenderApps(bool keepScroll)
        {
            if (closing)
                return;

            string current = CurrentAppPath();
            int column = apps.CurrentCell == null ? 0 : apps.CurrentCell.ColumnIndex;
            int first = apps.FirstDisplayedScrollingRowIndex;
            string top = keepScroll && first >= 0 ? ((AppUsage)apps.Rows[first].Tag).Path : null;
            var entries = new List<AppUsage>(lastSnapshot.Apps);
            var seen = new HashSet<string>(entries.Select(app => app.Path), StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> savedPaths = EditPaths.Concat(EditExclusions);
            if (editedProfile != null)
                savedPaths = savedPaths.Concat(editedProfile.ProtectedPaths);
            foreach (string path in savedPaths)
            {
                if (seen.Add(path))
                    entries.Add(new AppUsage
                    {
                        Name = Path.GetFileNameWithoutExtension(path), Path = path,
                        IsDefault = EditExclusions.Contains(path)
                    });
            }

            string term = search.Text.Trim();
            IEnumerable<AppUsage> visible = entries.Where(app =>
                (!selectedOnly.Checked || IsSelected(app)) && (term.Length == 0 ||
                    app.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    app.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
            int currentIndex = -1;
            int topIndex = -1;
            int selectedCount = 0;
            int selectableCount = 0;
            rendering = true;
            apps.SuspendLayout();
            try
            {
                apps.Rows.Clear();
                foreach (AppUsage app in OrderApps(visible))
                {
                    bool automatic = IsAutomatic(app);
                    bool selected = IsSelected(app);
                    bool protectedInEditor = IsProtectedInEditor(app.Path);
                    if (!protectedInEditor)
                        selectableCount++;
                    if (selected)
                        selectedCount++;
                    string note = GetAppStatus(app, selected, automatic);
                    string cpu = app.ProcessCount == 0 ? "—" : app.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%";
                    string memory = app.ProcessCount == 0 ? "—" : FormatMemory(app.WorkingSetBytes);
                    int index = apps.Rows.Add(selected, app.Name, app.ProcessCount, cpu, memory, note);
                    DataGridViewRow row = apps.Rows[index];
                    row.Tag = app;
                    row.Cells[0].ReadOnly = protectedInEditor;
                    row.Cells[0].ToolTipText = protectedInEditor ? "由游戏配置保护" : automatic ? "取消勾选可单独排除此程序" : "记住此程序的优化选择";
                    row.Cells[1].ToolTipText = app.Path;
                    if (app.IsAdjusted)
                        row.Cells[5].Style.ForeColor = Color.FromArgb(22, 112, 76);
                    if (String.Equals(app.Path, current, StringComparison.OrdinalIgnoreCase))
                        currentIndex = index;
                    if (String.Equals(app.Path, top, StringComparison.OrdinalIgnoreCase))
                        topIndex = index;
                }
                if (keepScroll && currentIndex >= 0)
                    apps.CurrentCell = apps.Rows[currentIndex].Cells[column];
                else
                {
                    apps.CurrentCell = null;
                    apps.ClearSelection();
                    if (apps.Rows.Count > 0)
                        apps.FirstDisplayedScrollingRowIndex = 0;
                }
                if (topIndex >= 0)
                    apps.FirstDisplayedScrollingRowIndex = topIndex;
            }
            finally
            {
                apps.ResumeLayout();
                rendering = false;
            }

            string counts = String.Format(CultureInfo.CurrentCulture, "显示 {0}/{1} 个应用 · 已勾选 {2} 个",
                apps.Rows.Count, entries.Count, entries.Count(IsSelected));
            listSummary.Text = counts + (apps.Rows.Count == 0 ? "\n没有符合当前筛选条件的应用。" : "\n全选和取消全选只影响当前筛选结果。右键可打开程序所在文件夹。");
            bulkSelect.Enabled = selectedCount < selectableCount;
            bulkClear.Enabled = selectedCount > 0;
        }

        private IEnumerable<AppUsage> OrderApps(IEnumerable<AppUsage> entries)
        {
            IOrderedEnumerable<AppUsage> ordered;
            switch (sortColumn)
            {
                case AppSortColumn.Name:
                    ordered = OrderByApp(entries, app => app.Name, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case AppSortColumn.Processes:
                    ordered = OrderByApp(entries, app => app.ProcessCount);
                    break;
                case AppSortColumn.Memory:
                    ordered = OrderByApp(entries, app => app.WorkingSetBytes);
                    break;
                case AppSortColumn.Status:
                    ordered = OrderByApp(entries, app => GetAppStatus(app, IsSelected(app), IsAutomatic(app)), StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    ordered = OrderByApp(entries, app => app.CpuPercent).ThenByDescending(app => app.WorkingSetBytes);
                    break;
            }
            return ordered.ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.Path, StringComparer.OrdinalIgnoreCase);
        }

        private IOrderedEnumerable<AppUsage> OrderByApp<TKey>(IEnumerable<AppUsage> entries, Func<AppUsage, TKey> key, IComparer<TKey> comparer = null)
        {
            return sortDescending ? entries.OrderByDescending(key, comparer) : entries.OrderBy(key, comparer);
        }

        private bool IsAutomatic(AppUsage app)
        {
            return app.IsDefault && EditIncludesDefaults && !EditExclusions.Contains(app.Path) && !IsProtectedInEditor(app.Path);
        }

        private bool IsSelected(AppUsage app)
        {
            return !IsProtectedInEditor(app.Path) && (EditPaths.Contains(app.Path) || IsAutomatic(app));
        }

        private bool IsProtectedInEditor(string path)
        {
            return (editedProfile != null && editedProfile.ProtectedPaths.Contains(path)) ||
                gameProfiles.Profiles.Exists(profile => SamePath(profile.GamePath, path));
        }

        private static bool SamePath(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private string GetAppStatus(AppUsage app, bool selected, bool automatic)
        {
            if (IsProtectedInEditor(app.Path))
                return "名单保护";
            if (app.IsProtected)
                return "运行配置保护";
            if (app.IsAdjusted)
                return "已降优先级";
            if (!selected && EditExclusions.Contains(app.Path))
                return "已排除";
            if (app.ProcessCount == 0)
                return "未运行";
            if (app.IsForeground)
                return "前台保护";
            if (selected && active)
                return automatic ? "自动勾选" : "已选中";
            if (selected)
                return "已暂停";
            return "—";
        }

        private static string FormatMemory(long bytes)
        {
            const double megabyte = 1024 * 1024;
            if (bytes >= megabyte * 1024)
                return (bytes / (megabyte * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            return (bytes / megabyte).ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        private const double BytesPerGigabyte = 1024.0 * 1024 * 1024;

        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (allowClose || args.CloseReason == CloseReason.WindowsShutDown)
                return;

            args.Cancel = true;
            if (!closing && args.CloseReason == CloseReason.UserClosing && trayIcon.Visible)
                Hide();
            else
                ExitApplication();
        }

        private async void ExitApplication()
        {
            if (closing)
                return;

            closing = true;
            trayOpen.Enabled = false;
            trayToggle.Enabled = false;
            trayExit.Enabled = false;
            trayIcon.Text = "游戏优化助手：正在恢复优先级";
            Enabled = false;
            try
            {
                int remaining = await StopMonitoringAsync();
                if (remaining > 0)
                    ShowRestoreWarning(String.Format("仍有 {0} 个进程的优先级待恢复，下次打开会重试。", remaining));
            }
            catch (Exception error)
            {
                ShowRestoreWarning("恢复优先级时遇到错误。\n\n" + error.Message);
            }

            allowClose = true;
            Close();
        }

        private void ShowRestoreWarning(string message)
        {
            IWin32Window owner = Visible ? this : null;
            MessageBox.Show(owner, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void SetStatus(string title, string details, Color color)
        {
            OptimizationStatus = title;
            if (trayStatus != null)
                trayStatus.Text = title;
            status.Text = title;
            status.ForeColor = color;
            summary.Text = details;
        }

        private Label MakeLabel(string title, Color color, float size, FontStyle style)
        {
            var label = new Label { Text = title };
            StyleLabel(label, color, size, style);
            return label;
        }

        private void StyleLabel(Label label, Color color, float size, FontStyle style)
        {
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.ForeColor = color;
            label.Font = new Font(Font.FontFamily, size, style);
        }

        private static void StyleButton(Button button, string label, bool primary)
        {
            button.Text = label;
            button.Margin = new Padding(0);
            button.Cursor = Cursors.Hand;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(186, 201, 225);
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
        }
    }
}
