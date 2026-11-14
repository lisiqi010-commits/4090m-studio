using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GameBoost
{
    internal sealed class GameProfilesDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 37, 61);
        private static readonly Color Muted = Color.FromArgb(77, 95, 119);
        private static readonly Color Surface = Color.FromArgb(244, 247, 251);
        private static readonly Color Accent = Color.FromArgb(37, 88, 221);

        private readonly GameProfiles data;
        private readonly Settings settings;
        private readonly List<GameProfile> staged;
        private readonly CheckBox autoMode = new CheckBox
        {
            Name = "AutoGameMode", Text = "自动识别已配置的游戏", AutoSize = true, Anchor = AnchorStyles.Left
        };
        private readonly ListBox games = new ListBox
        {
            Name = "GameProfileList", AccessibleName = "游戏列表", Dock = DockStyle.Fill, IntegralHeight = false
        };
        private readonly ListBox protectedApps = new ListBox
        {
            Name = "ProtectedProgramList", AccessibleName = "受保护的程序路径", Dock = DockStyle.Fill,
            IntegralHeight = false, HorizontalScrollbar = true
        };
        private readonly TextBox gamePath = new TextBox
        {
            AccessibleName = "游戏程序路径", Dock = DockStyle.Fill, ReadOnly = true
        };
        private readonly Label ruleSummary = new Label();
        private readonly Button removeGame = MakeButton("RemoveGame", "移除游戏");
        private readonly Button addProtected = MakeButton("AddProtectedProgram", "添加保护…");
        private readonly Button removeProtected = MakeButton("RemoveProtectedProgram", "移除保护");
        private readonly Button copyManual = MakeButton("CopyManualSelection", "复制手动规则");

        internal GameProfilesDialog(GameProfiles data, Settings settings)
        {
            this.data = data;
            this.settings = settings;
            staged = data.Profiles.Select(profile => profile.Clone()).ToList();
            autoMode.Checked = data.AutoMode;

            Text = "游戏配置";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(780, 430);
            MinimumSize = new Size(720, 410);
            BackColor = Surface;
            ForeColor = Ink;

            BuildLayout();
            games.SelectedIndexChanged += delegate { RefreshSelectedProfile(); };
            protectedApps.SelectedIndexChanged += delegate { removeProtected.Enabled = protectedApps.SelectedIndex >= 0; };
            removeGame.Click += delegate { RemoveGame(); };
            addProtected.Click += delegate { AddProtected(); };
            removeProtected.Click += delegate { RemoveProtected(); };
            copyManual.Click += delegate
            {
                GameProfile profile = SelectedProfile;
                if (profile != null)
                {
                    CopyManualRules(profile);
                    RefreshRuleSummary(profile);
                }
            };
            RenderGames(staged.Count > 0 ? 0 : -1);
        }

        private GameProfile SelectedProfile
        {
            get { return games.SelectedIndex < 0 ? null : staged[games.SelectedIndex]; }
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16, 12, 16, 10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.Controls.Add(autoMode, 0, 0);
            layout.Controls.Add(MakeLabel("检测到游戏时应用对应配置。后台应用的优化规则可在主窗口编辑。", Muted), 0, 1);

            var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panes.Controls.Add(BuildGamePane(), 0, 0);
            panes.Controls.Add(BuildProtectionPane(), 1, 0);
            layout.Controls.Add(panes, 0, 2);

            Button save = MakeButton("SaveGameProfiles", "保存");
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.BackColor = Accent;
            save.ForeColor = Color.White;
            save.Click += delegate { SaveAndClose(); };
            Button cancel = MakeButton("CancelGameProfiles", "取消");
            cancel.DialogResult = DialogResult.Cancel;
            var footer = MakeButtons(save, cancel);
            footer.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            footer.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(footer, 0, 3);
            AcceptButton = save;
            CancelButton = cancel;
            Controls.Add(layout);
        }

        private TableLayoutPanel BuildGamePane()
        {
            var pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 12, 0) };
            pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            pane.Controls.Add(MakeLabel("已配置的游戏", Ink), 0, 0);
            pane.Controls.Add(games, 0, 1);
            gamePath.Margin = new Padding(3, 6, 3, 0);
            pane.Controls.Add(gamePath, 0, 2);

            Button addGame = MakeButton("AddGame", "添加游戏…");
            addGame.Click += delegate { AddGame(); };
            pane.Controls.Add(MakeButtons(addGame, removeGame), 0, 3);
            return pane;
        }

        private TableLayoutPanel BuildProtectionPane()
        {
            var pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            pane.Controls.Add(MakeLabel("保持原优先级的程序（例如语音聊天或录屏工具）", Ink), 0, 0);
            pane.Controls.Add(protectedApps, 0, 1);
            ruleSummary.Dock = DockStyle.Fill;
            ruleSummary.ForeColor = Muted;
            ruleSummary.TextAlign = ContentAlignment.MiddleLeft;
            ruleSummary.AutoEllipsis = true;
            pane.Controls.Add(ruleSummary, 0, 2);
            copyManual.AccessibleDescription = "用当前手动规则替换此游戏的优化列表和同步软件设置";
            pane.Controls.Add(MakeButtons(addProtected, removeProtected, copyManual), 0, 3);
            return pane;
        }

        private void AddGame()
        {
            string path = PickProgram("选择游戏的实际运行程序");
            if (path == null)
                return;

            int index = staged.FindIndex(profile => String.Equals(profile.GamePath, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                games.SelectedIndex = index;
                return;
            }

            var added = new GameProfile { GamePath = path };
            CopyManualRules(added);
            staged.Add(added);
            RenderGames(staged.Count - 1);
        }

        private void RemoveGame()
        {
            int index = games.SelectedIndex;
            if (index < 0)
                return;

            games.Items.Clear();
            staged.RemoveAt(index);
            RenderGames(Math.Min(index, staged.Count - 1));
        }

        private void AddProtected()
        {
            GameProfile profile = SelectedProfile;
            if (profile == null)
                return;

            string path = PickProgram("选择要保持原优先级的程序");
            if (path == null)
                return;

            if (String.Equals(path, profile.GamePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "游戏程序本身已受到保护。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            profile.ProtectedPaths.Add(path);
            RenderProtected(profile);
            for (int index = 0; index < protectedApps.Items.Count; index++)
            {
                if (String.Equals(((ProtectedProgram)protectedApps.Items[index]).Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    protectedApps.SelectedIndex = index;
                    break;
                }
            }
        }

        private void RemoveProtected()
        {
            GameProfile profile = SelectedProfile;
            var program = protectedApps.SelectedItem as ProtectedProgram;
            if (profile != null && program != null)
            {
                profile.ProtectedPaths.Remove(program.Path);
                RenderProtected(profile);
            }
        }

        private string PickProgram(string title)
        {
            using (var picker = new OpenFileDialog
            {
                Title = title, Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true, RestoreDirectory = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK)
                    return null;

                string path = Path.GetFullPath(picker.FileName);
                if (String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    return path;

                MessageBox.Show(this, "请选择一个 .exe 程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
        }

        private void CopyManualRules(GameProfile profile)
        {
            profile.IncludeDefaults = settings.IncludeDefaults;
            profile.Paths.Clear();
            profile.Paths.UnionWith(settings.Paths);
            profile.ExcludedDefaults.Clear();
            profile.ExcludedDefaults.UnionWith(settings.ExcludedDefaults);
        }

        private void RenderGames(int index)
        {
            games.BeginUpdate();
            try
            {
                games.Items.Clear();
                foreach (GameProfile profile in staged)
                    games.Items.Add(Path.GetFileName(profile.GamePath));
                games.SelectedIndex = index;
            }
            finally
            {
                games.EndUpdate();
            }
            RefreshSelectedProfile();
        }

        private void RefreshSelectedProfile()
        {
            GameProfile profile = SelectedProfile;
            bool selected = profile != null;
            gamePath.Text = selected ? profile.GamePath : String.Empty;
            removeGame.Enabled = addProtected.Enabled = copyManual.Enabled = selected;
            RenderProtected(profile);
            RefreshRuleSummary(profile);
        }

        private void RefreshRuleSummary(GameProfile profile)
        {
            ruleSummary.Text = profile == null ? "添加游戏后可设置保护名单。" :
                String.Format("已选 {0} 个优化目标 · 同步软件自动优化{1}", profile.Paths.Count, profile.IncludeDefaults ? "已开启" : "已关闭");
        }

        private void RenderProtected(GameProfile profile)
        {
            protectedApps.BeginUpdate();
            try
            {
                protectedApps.Items.Clear();
                if (profile != null)
                {
                    protectedApps.Items.AddRange(profile.ProtectedPaths.Select(path => new ProtectedProgram(path))
                        .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(program => program.Path, StringComparer.OrdinalIgnoreCase).ToArray());
                }
            }
            finally
            {
                protectedApps.EndUpdate();
            }
            removeProtected.Enabled = protectedApps.SelectedIndex >= 0;
        }

        private void SaveAndClose()
        {
            try
            {
                data.SaveChanges(autoMode.Checked, staged);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "无法保存游戏配置。\n\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static Button MakeButton(string name, string text)
        {
            return new Button
            {
                Name = name, Text = text, AutoSize = true, MinimumSize = new Size(86, 32),
                Margin = new Padding(3, 3, 6, 3), UseVisualStyleBackColor = true
            };
        }

        private static FlowLayoutPanel MakeButtons(params Button[] buttons)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0)
            };
            panel.Controls.AddRange(buttons);
            return panel;
        }

        private static Label MakeLabel(string text, Color color)
        {
            return new Label
            {
                Text = text, ForeColor = color, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
        }

        private sealed class ProtectedProgram
        {
            internal readonly string Name;
            internal readonly string Path;

            internal ProtectedProgram(string path)
            {
                Name = System.IO.Path.GetFileName(path);
                Path = path;
            }

            public override string ToString()
            {
                return Name + "  ·  " + Path;
            }
        }
    }
}
