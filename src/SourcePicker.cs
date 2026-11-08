using System;
using System.Drawing;
using System.Windows.Forms;

namespace RtxHub
{
    public enum SourceKind { Files, PotPlayer, Browser }

    public sealed class SourceSelection
    {
        public SourceKind Kind;
        public string[] Files = new string[0];
        public IntPtr BrowserHandle;
    }

    /// <summary>Selects a source only. This UI never captures, installs, or starts playback.</summary>
    public static class SourcePicker
    {
        static readonly Color Background = Color.FromArgb(18, 21, 25);
        static readonly Color Surface = Color.FromArgb(27, 33, 38);
        static readonly Color Accent = Color.FromArgb(178, 235, 126);
        static readonly Color Foreground = Color.FromArgb(235, 240, 242);
        static readonly Color Muted = Color.FromArgb(151, 164, 174);

        public static SourceSelection Select(IWin32Window owner)
        {
            using (var dialog = new SourceDialog(false))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Selection : null;
        }

        /// <summary>Pure UI preview: no file picker, browser enumeration, or capture.</summary>
        public static Form CreatePreview() { return new SourceDialog(true); }

        static Label TextLabel(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = Padding.Empty };
        }

        static Button PlainButton(string text, bool primary)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(116, 38), Height = 38, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.FromArgb(22, 39, 20) : Foreground, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(60, 70, 78);
            return button;
        }

        static void StyleDialog(Form form, string title, Size size)
        {
            form.Text = title;
            form.ClientSize = size;
            form.StartPosition = FormStartPosition.CenterParent;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.BackColor = Background;
            form.ForeColor = Foreground;
            form.Font = new Font("Microsoft YaHei UI", 10);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }

        sealed class SourceDialog : Form
        {
            internal SourceSelection Selection;

            internal SourceDialog(bool preview)
            {
                StyleDialog(this, "选择来源 · 4090M Studio", new Size(680, 460));
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 20), ColumnCount = 1, RowCount = 6 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                Controls.Add(layout);

                var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
                heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
                heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                heading.Controls.Add(TextLabel("选择播放来源", 19, Foreground, true), 0, 0);
                heading.Controls.Add(TextLabel("本地影片可多选；游戏、播放器和网页各按所选来源处理。", 9, Muted, false), 0, 1);
                layout.Controls.Add(heading, 0, 0);

                var files = new SourceButton("本地游戏 / 视频", "选择游戏程序，或多选本地影片", true);
                var pot = new SourceButton("PotPlayer", "打开增强播放器，使用原生播放列表", false);
                var browser = new SourceButton("网页视频", "选择一个浏览器窗口，在独立窗口中增强显示", false);
                files.AccessibleName = "选择本地游戏或多个视频文件";
                pot.AccessibleName = "选择 PotPlayer 来源";
                browser.AccessibleName = "选择网页视频浏览器窗口";
                layout.Controls.Add(files, 0, 1);
                layout.Controls.Add(pot, 0, 2);
                layout.Controls.Add(browser, 0, 3);

                var note = TextLabel("网页增强使用独立显示窗口，声音和播放控制留在浏览器。\r\n受保护内容或最小化窗口可能无法显示画面。", 9, Muted, false);
                note.Margin = new Padding(0, 10, 0, 3);
                layout.Controls.Add(note, 0, 4);

                var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
                var cancel = PlainButton("返回", false); cancel.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 5); CancelButton = cancel;

                if (preview) return; // The preview remains entirely free of source discovery and launch calls.
                files.Click += delegate
                {
                    using (var picker = new OpenFileDialog
                    {
                        Title = "选择一个游戏，或选择多个本地视频",
                        Filter = "游戏与视频|*.exe;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.m2ts;*.mts;*.flv;*.mpg;*.mpeg;*.vob|游戏程序|*.exe|本地视频|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.m2ts;*.mts;*.flv;*.mpg;*.mpeg;*.vob",
                        Multiselect = true,
                        CheckFileExists = true,
                        RestoreDirectory = true
                    })
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) return;
                        // Count/mixed-game validation belongs to the business layer, including its 500-file ceiling.
                        Selection = new SourceSelection { Kind = SourceKind.Files, Files = picker.FileNames };
                        DialogResult = DialogResult.OK;
                    }
                };
                pot.Click += delegate { Selection = new SourceSelection { Kind = SourceKind.PotPlayer }; DialogResult = DialogResult.OK; };
                browser.Click += delegate
                {
                    using (var windows = new BrowserDialog())
                    {
                        if (windows.ShowDialog(this) != DialogResult.OK) return;
                        Selection = new SourceSelection { Kind = SourceKind.Browser, BrowserHandle = windows.SelectedHandle };
                        DialogResult = DialogResult.OK;
                    }
                };
            }
        }

        sealed class BrowserChoice
        {
            internal readonly IntPtr Handle;
            readonly string title;
            internal BrowserChoice(BrowserWindow window) { Handle = window.Handle; title = window.Title; }
            public override string ToString() { return title ?? "未命名浏览器窗口"; }
        }

        sealed class BrowserDialog : Form
        {
            readonly ListBox windows;
            readonly Label state;
            readonly Button use;
            internal IntPtr SelectedHandle;

            internal BrowserDialog()
            {
                StyleDialog(this, "选择浏览器窗口 · 网页视频", new Size(760, 470));
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 20), ColumnCount = 1, RowCount = 5 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                Controls.Add(layout);
                layout.Controls.Add(TextLabel("选择要观看的浏览器窗口", 17, Foreground, true), 0, 0);
                layout.Controls.Add(TextLabel("将只使用您选定窗口的当前画面，在独立增强窗口显示。\r\n声音和播放控制仍在浏览器；受保护视频或最小化窗口可能不可用。", 9, Muted, false), 0, 1);
                windows = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, HorizontalScrollbar = true, Font = new Font("Microsoft YaHei UI", 10), AccessibleName = "可用浏览器窗口标题列表" };
                layout.Controls.Add(windows, 0, 2);
                state = TextLabel("点击刷新可重新列出已打开的浏览器窗口。", 9, Muted, false); state.Margin = new Padding(0, 9, 0, 0); layout.Controls.Add(state, 0, 3);
                var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
                use = PlainButton("使用所选窗口", true); use.Enabled = false;
                var refresh = PlainButton("刷新列表", false);
                var back = PlainButton("返回", false); back.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(use); footer.Controls.Add(refresh); footer.Controls.Add(back); layout.Controls.Add(footer, 0, 4);
                AcceptButton = use; CancelButton = back;
                use.Click += delegate { Choose(); };
                windows.DoubleClick += delegate { Choose(); };
                windows.SelectedIndexChanged += delegate { use.Enabled = windows.SelectedItem is BrowserChoice; };
                refresh.Click += delegate { RefreshWindows(); };
                Shown += delegate { RefreshWindows(); }; // Only constructed after the user explicitly chooses the browser source.
            }

            void RefreshWindows()
            {
                windows.Items.Clear(); use.Enabled = false;
                try
                {
                    BrowserWindow[] found = WebVideoSupport.ListBrowserWindows();
                    foreach (var window in found) windows.Items.Add(new BrowserChoice(window));
                    if (windows.Items.Count == 0) state.Text = "未找到可用浏览器窗口。请先打开并恢复浏览器窗口，再点击“刷新列表”。";
                    else { windows.SelectedIndex = 0; state.Text = "已找到 " + windows.Items.Count + " 个窗口。请按标题确认目标；确认前不会开始采集。"; }
                }
                catch (Exception ex) { state.Text = "窗口列表未能刷新：" + ex.Message + " 可点击“刷新列表”重试。"; }
            }

            void Choose()
            {
                var chosen = windows.SelectedItem as BrowserChoice;
                if (chosen == null) { state.Text = "请先选择一个浏览器窗口，或刷新列表。"; return; }
                SelectedHandle = chosen.Handle;
                DialogResult = DialogResult.OK;
            }
        }

        sealed class SourceButton : Button
        {
            readonly string description;
            readonly bool primary;
            internal SourceButton(string title, string description, bool primary)
            {
                Text = title; this.description = description; this.primary = primary;
                Dock = DockStyle.Fill; Margin = new Padding(0, 0, 0, 8); FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; TabStop = true;
                AccessibleDescription = description;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            protected override void OnPaint(PaintEventArgs args)
            {
                Color background = primary ? Accent : Surface;
                Color titleColor = primary ? Color.FromArgb(22, 39, 20) : Foreground;
                args.Graphics.Clear(background);
                using (var titleFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold))
                using (var detailFont = new Font("Microsoft YaHei UI", 9))
                {
                    TextRenderer.DrawText(args.Graphics, Text, titleFont, new Rectangle(18, 8, Width - 36, 30), titleColor, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(args.Graphics, description, detailFont, new Rectangle(19, 40, Width - 38, 24), primary ? Color.FromArgb(54, 77, 48) : Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }
                if (Focused) ControlPaint.DrawFocusRectangle(args.Graphics, new Rectangle(5, 5, Width - 11, Height - 11), titleColor, background);
            }
        }
    }
}
