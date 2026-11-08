using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RtxHub
{
    public sealed class SimpleHome : Form
    {
        static readonly Color Background = Color.FromArgb(18, 21, 25);
        static readonly Color Accent = Color.FromArgb(178, 235, 126);
        static readonly Color Foreground = Color.FromArgb(235, 240, 242);
        static readonly Color Muted = Color.FromArgb(151, 164, 174);
        readonly bool renderOnly;
        readonly HomeCard open;
        readonly HomeCard cancel;
        readonly Label selected;
        readonly Label status;
        readonly Label spinner;
        readonly Timer pendingTimer;
        readonly Timer animationTimer;
        bool busy;
        int animationFrame;

        public SimpleHome(bool renderOnly)
        {
            this.renderOnly = renderOnly;
            Text = "4090M Studio";
            ClientSize = new Size(860, 540);
            MinimumSize = new Size(676, 519);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = Foreground;
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            AllowDrop = true;

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.Padding = new Padding(32, 26, 32, 24);
            shell.ColumnCount = 1;
            shell.RowCount = 5;
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            Controls.Add(shell);

            Panel heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Label brand = MakeLabel("4090M  /  STUDIO", 10, Accent, true);
            brand.SetBounds(0, 0, 500, 24);
            Label title = MakeLabel("游戏与视频，点一下就好。", 25, Foreground, true);
            title.SetBounds(-3, 30, 710, 49);
            heading.Controls.Add(brand);
            heading.Controls.Add(title);
            heading.Resize += delegate { title.SetBounds(-3, 30, heading.ClientSize.Width + 3, 49); };
            shell.Controls.Add(heading, 0, 0);

            Panel cards = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
            open = new HomeCard("选择游戏或视频", "游戏补帧 1–6× · 中文增强\r\nPotPlayer · 网页视频", true);
            cancel = new HomeCard("取消全部注入", "运行中的程序退出后自动撤回\r\n等待期间请保持本窗口打开", false);
            open.TabIndex = 0;
            cancel.TabIndex = 1;
            cards.Controls.Add(open);
            cards.Controls.Add(cancel);
            cards.Resize += delegate
            {
                int cardWidth = Math.Max(1, (cards.ClientSize.Width - 18) / 2);
                open.SetBounds(0, 0, cardWidth, cards.ClientSize.Height);
                cancel.SetBounds(cardWidth + 18, 0, cards.ClientSize.Width - cardWidth - 18, cards.ClientSize.Height);
            };
            shell.Controls.Add(cards, 0, 1);

            selected = MakeLabel("也可以拖入一个游戏，或多段本地视频", 9, Muted, false);
            selected.Dock = DockStyle.Fill;
            selected.Margin = Padding.Empty;
            selected.AutoEllipsis = true;
            selected.TextAlign = ContentAlignment.MiddleLeft;
            shell.Controls.Add(selected, 0, 2);

            Panel state = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            spinner = MakeLabel("●", 11, Accent, false);
            spinner.SetBounds(0, 10, 28, 26);
            status = MakeLabel("就绪，选择一个游戏或视频。", 9.5f, Foreground, false);
            status.AutoEllipsis = true;
            status.SetBounds(27, 6, 700, 34);
            status.TextAlign = ContentAlignment.MiddleLeft;
            state.Controls.Add(spinner);
            state.Controls.Add(status);
            state.Resize += delegate { status.SetBounds(27, 6, Math.Max(1, state.ClientSize.Width - 27), 34); };
            shell.Controls.Add(state, 0, 3);

            Label footer = MakeLabel("选择即加载并启动；取消仅处理本工具管理的注入。", 8.5f, Muted, false);
            footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty;
            footer.TextAlign = ContentAlignment.BottomLeft;
            shell.Controls.Add(footer, 0, 4);

            animationTimer = new Timer { Interval = 180 };
            animationTimer.Tick += delegate
            {
                string[] frames = { "◐", "◓", "◑", "◒" };
                spinner.Text = frames[animationFrame++ % frames.Length];
            };
            pendingTimer = new Timer { Interval = 3000 };
            pendingTimer.Tick += async delegate
            {
                if (busy || renderOnly) return;
                try
                {
                    if (SimpleActions.HasPendingCancellation)
                        await RunOperation(delegate(Action<string> progress) { return SimpleActions.PollPending(progress); }, null);
                }
                catch (Exception exception) { ShowStatus(exception.Message); }
            };
            open.Click += delegate { ChooseFile(); };
            cancel.Click += async delegate
            {
                if (!renderOnly && !busy)
                    await RunOperation(delegate(Action<string> progress) { return SimpleActions.CancelAll(progress); }, "正在取消，请稍候…");
            };
            DragEnter += OnFileDragEnter;
            DragDrop += OnFileDrop;
            open.AllowDrop = true;
            open.DragEnter += OnFileDragEnter;
            open.DragDrop += OnFileDrop;
            Shown += delegate { if (!renderOnly) pendingTimer.Start(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!busy) return;
                args.Cancel = true;
                ShowStatus("正在完成操作，完成后即可关闭。");
            };
        }

        static Label MakeLabel(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent };
        }

        public static bool SupportsSelection(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string extension;
            try { extension = Path.GetExtension(path).ToLowerInvariant(); }
            catch (ArgumentException) { return false; }
            switch (extension)
            {
                case ".exe": case ".mp4": case ".mkv": case ".mov": case ".avi":
                case ".webm": case ".m4v": case ".wmv": case ".ts": case ".m2ts":
                case ".flv": case ".mpg": case ".mpeg": case ".mts": case ".vob":
                    return true;
                default: return false;
            }
        }

        void ChooseFile()
        {
            if (busy || renderOnly) return;
            SourceSelection choice = SourcePicker.Select(this);
            if (choice == null) return;
            if (choice.Kind == SourceKind.Files) OpenSelections(choice.Files);
            else if (choice.Kind == SourceKind.PotPlayer) OpenExtra(false, IntPtr.Zero);
            else OpenExtra(true, choice.BrowserHandle);
        }

        async void OpenExtra(bool browser, IntPtr handle)
        {
            if (busy || renderOnly) return;
            selected.Text = browser ? "已选择：网页视频（声音由浏览器播放）" : "已选择：PotPlayer 与原生播放列表";
            await RunOperation(delegate(Action<string> progress)
            {
                return browser ? SimpleActions.OpenBrowser(handle, progress) : SimpleActions.OpenPotPlayer(progress);
            }, "正在加载，请稍候…");
        }

        public static bool SupportsSelections(string[] paths)
        {
            if(paths==null||paths.Length==0||paths.Length>500)return false;
            foreach(string path in paths)
                if(!SupportsSelection(path)||!File.Exists(path)||(paths.Length>1&&String.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase)))return false;
            return true;
        }

        void OnFileDragEnter(object sender, DragEventArgs args)
        {
            args.Effect = DragDropEffects.None;
            if (busy || renderOnly || !args.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = args.Data.GetData(DataFormats.FileDrop) as string[];
            if (SupportsSelections(paths))
                args.Effect = DragDropEffects.Copy;
        }

        void OnFileDrop(object sender, DragEventArgs args)
        {
            if (busy || renderOnly) return;
            string[] paths = args.Data.GetData(DataFormats.FileDrop) as string[];
            if (!SupportsSelections(paths))
            {
                ShowStatus("请选择一个游戏，或多段存在的本地视频。");
                return;
            }
            OpenSelections(paths);
        }

        void OpenSelection(string path) { OpenSelections(new[]{path}); }

        async void OpenSelections(string[] paths)
        {
            if (busy || renderOnly) return;
            if (!SupportsSelections(paths))
            {
                ShowStatus("请选择一个游戏，或多段存在的本地视频。");
                return;
            }
            int multiplier=1;
            if(paths.Length==1&&String.Equals(Path.GetExtension(paths[0]),".exe",StringComparison.OrdinalIgnoreCase))
            {
                int? choice=GameLaunchOptions.Select(this,paths[0]);if(!choice.HasValue)return;multiplier=choice.Value;
            }
            selected.Text = "已选择：" + Path.GetFileName(paths[0]) + (paths.Length > 1 ? " 等 " + paths.Length + " 段视频" : (multiplier>1?" · 请求 "+multiplier+"× 补帧":""));
            await RunOperation(delegate(Action<string> progress) { return SimpleActions.OpenMany(paths, multiplier, progress); }, "正在加载，请稍候…");
        }

        async Task RunOperation(Func<Action<string>, string> operation, string initialStatus)
        {
            if (busy || renderOnly) return;
            busy = true;
            open.Enabled = false;
            cancel.Enabled = false;
            animationTimer.Start();
            if (!String.IsNullOrEmpty(initialStatus)) ShowStatus(initialStatus);
            try
            {
                string result = await Task.Run(delegate { return operation(PostProgress); });
                if (!IsDisposed && !Disposing && !String.IsNullOrWhiteSpace(result)) ShowStatus(result);
            }
            catch (Exception exception)
            {
                if (!IsDisposed && !Disposing) ShowStatus(exception.Message);
            }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing)
                {
                    animationTimer.Stop();
                    spinner.Text = "●";
                    open.Enabled = true;
                    cancel.Enabled = true;
                }
            }
        }

        void PostProgress(string text)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) ShowStatus(text); }); }
            catch (InvalidOperationException) { }
        }

        void ShowStatus(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            string display = text.Trim();
            int newline = display.IndexOfAny(new char[] { '\r', '\n' });
            if (newline >= 0) display = display.Substring(0, newline).Trim();
            if (display.Length > 180) display = display.Substring(0, 177) + "…";
            if (status.Text != display) status.Text = display;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (pendingTimer != null) pendingTimer.Dispose();
                if (animationTimer != null) animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        sealed class HomeCard : Button
        {
            readonly string detail;
            readonly bool primary;
            bool hovering;

            public HomeCard(string text, string detail, bool primary)
            {
                Text = text;
                this.detail = detail;
                this.primary = primary;
                AccessibleName = text;
                AccessibleDescription = detail.Replace("\r\n", "，");
                AccessibleRole = AccessibleRole.PushButton;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Cursor = Cursors.Hand;
                TabStop = true;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseEnter(EventArgs args) { hovering = true; Invalidate(); base.OnMouseEnter(args); }
            protected override void OnMouseLeave(EventArgs args) { hovering = false; Invalidate(); base.OnMouseLeave(args); }
            protected override void OnEnabledChanged(EventArgs args) { Invalidate(); base.OnEnabledChanged(args); }

            protected override void OnPaint(PaintEventArgs args)
            {
                Color background = primary ? (hovering ? Color.FromArgb(193, 247, 146) : Accent) : (hovering ? Color.FromArgb(36, 43, 48) : Color.FromArgb(27, 33, 38));
                Color titleColor = primary ? Color.FromArgb(22, 39, 20) : Foreground;
                Color detailColor = primary ? Color.FromArgb(54, 77, 48) : Muted;
                if (!Enabled)
                {
                    background = primary ? Color.FromArgb(105, 135, 80) : Color.FromArgb(25, 29, 33);
                    titleColor = primary ? Color.FromArgb(36, 52, 31) : Color.FromArgb(132, 142, 150);
                }
                args.Graphics.Clear(background);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen border = new Pen(primary ? background : Color.FromArgb(60, 70, 78)))
                    args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                using (Pen icon = new Pen(titleColor, 2))
                {
                    if (primary)
                    {
                        args.Graphics.DrawLines(icon, new Point[] { new Point(27, 44), new Point(27, 24), new Point(39, 24), new Point(45, 30), new Point(59, 30), new Point(59, 50), new Point(27, 50), new Point(27, 44) });
                    }
                    else
                    {
                        args.Graphics.DrawArc(icon, 27, 25, 29, 29, 205, 290);
                        args.Graphics.DrawLines(icon, new Point[] { new Point(25, 25), new Point(26, 36), new Point(37, 34) });
                    }
                    args.Graphics.DrawLine(icon, Width - 52, Height - 29, Width - 28, Height - 29);
                    args.Graphics.DrawLines(icon, new Point[] { new Point(Width - 35, Height - 36), new Point(Width - 28, Height - 29), new Point(Width - 35, Height - 22) });
                }
                using (Font titleFont = new Font("Microsoft YaHei UI", 19, FontStyle.Bold))
                using (Font detailFont = new Font("Microsoft YaHei UI", 9.5f))
                {
                    TextRenderer.DrawText(args.Graphics, Text, titleFont, new Rectangle(23, 75, Width - 43, 42), titleColor, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(args.Graphics, detail, detailFont, new Rectangle(25, 126, Width - 50, Math.Max(32, Height - 169)), detailColor, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                if (Focused) ControlPaint.DrawFocusRectangle(args.Graphics, new Rectangle(7, 7, Width - 15, Height - 15), titleColor, background);
            }
        }
    }
}
