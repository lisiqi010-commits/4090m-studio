using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RtxHub
{
    /// <summary>Collects a multiplier request only; never discovers files or invokes a backend.</summary>
    public static class GameLaunchOptions
    {
        public static int? Select(IWin32Window owner, string exe)
        {
            using (var dialog = new GameLaunchOptionsDialog(exe))
            {
                dialog.ShowDialog(owner);
                return dialog.SelectedMultiplier;
            }
        }

        public static Form CreatePreview() { return new GameLaunchOptionsDialog("演示游戏.exe"); }
    }

    internal sealed class GameLaunchOptionsDialog : Form
    {
        static readonly Color Background = Color.FromArgb(18, 21, 25);
        static readonly Color Surface = Color.FromArgb(27, 33, 38);
        static readonly Color Accent = Color.FromArgb(178, 235, 126);
        static readonly Color Foreground = Color.FromArgb(235, 240, 242);
        static readonly Color Muted = Color.FromArgb(151, 164, 174);
        readonly ComboBox multiplier;

        internal int? SelectedMultiplier
        {
            get { return DialogResult == DialogResult.OK && multiplier.SelectedIndex >= 0 ? (int?)(multiplier.SelectedIndex + 1) : null; }
        }

        internal GameLaunchOptionsDialog(string exe)
        {
            Text = "游戏补帧 · 4090M Studio";
            ClientSize = new Size(680, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Background; ForeColor = Foreground;
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 22),
                ColumnCount = 1, RowCount = 8, Margin = Padding.Empty
            };
            foreach (int height in new[] { 44, 32, 26, 44, 44, 96 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(layout);

            layout.Controls.Add(Label("选择游戏补帧倍率", 19, Foreground, true), 0, 0);
            string filename;
            try { filename = String.IsNullOrWhiteSpace(exe) ? "未指定游戏" : Path.GetFileName(exe); }
            catch (ArgumentException) { filename = "未指定游戏"; }
            var game = Label("游戏：" + filename, 9, Muted, false);
            game.AutoEllipsis = true;
            layout.Controls.Add(game, 0, 1);
            layout.Controls.Add(Label("输出倍率", 9, Muted, false), 0, 2);

            multiplier = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28,
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8),
                Font = new Font("Microsoft YaHei UI", 12), BackColor = Surface,
                ForeColor = Foreground, AccessibleName = "游戏补帧倍率", TabIndex = 0
            };
            multiplier.Items.AddRange(new object[]
            {
                "1×  关闭本工具补帧", "2×  请求双倍输出", "3×  请求三倍输出",
                "4×  请求四倍输出", "5×  请求五倍输出", "6×  请求六倍输出"
            });
            multiplier.DrawItem += delegate(object sender, DrawItemEventArgs args)
            {
                if (args.Index < 0) return;
                bool chosen = (args.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(chosen ? Accent : Surface)) args.Graphics.FillRectangle(brush, args.Bounds);
                var bounds = args.Bounds; bounds.X += 8; bounds.Width -= 8;
                TextRenderer.DrawText(args.Graphics, multiplier.Items[args.Index].ToString(), args.Font, bounds,
                    chosen ? Color.FromArgb(22, 39, 20) : Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                args.DrawFocusRectangle();
            };
            multiplier.SelectedIndex = 0;
            layout.Controls.Add(multiplier, 0, 3);

            var selected = Label("关闭本工具补帧；游戏自身的帧生成开关保持独立。", 9.5f, Accent, false);
            selected.Margin = new Padding(0, 3, 0, 0);
            layout.Controls.Add(selected, 0, 4);
            multiplier.SelectedIndexChanged += delegate
            {
                selected.Text = multiplier.SelectedIndex == 0
                    ? "关闭本工具补帧；游戏自身的帧生成开关保持独立。"
                    : "请求 " + (multiplier.SelectedIndex + 1) + "× 输出；实际倍率需进入游戏后验证。";
            };

            var note = Label(
                "2–6× 为实验性社区方案，需要兼容的 DX12 游戏和原生 DLSS 帧生成，并在游戏内开启该功能。\r\n\r\n" +
                "RTX 40 多帧生成没有官方支持；实际画面、流畅度和延迟需在游戏中验证。", 9, Muted, false);
            note.Margin = new Padding(0, 5, 0, 0);
            layout.Controls.Add(note, 0, 5);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Margin = Padding.Empty
            };
            var confirm = Button("加载并启动", true);
            confirm.DialogResult = DialogResult.OK; confirm.TabIndex = 1;
            var cancel = Button("返回", false);
            cancel.DialogResult = DialogResult.Cancel; cancel.TabIndex = 2;
            footer.Controls.Add(confirm); footer.Controls.Add(cancel);
            layout.Controls.Add(footer, 0, 7);
            AcceptButton = confirm; CancelButton = cancel;
        }

        static Label Label(string text, float size, Color color, bool bold)
        {
            return new Label
            {
                Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
        }

        static Button Button(string text, bool primary)
        {
            var button = new Button
            {
                Text = text, Size = new Size(primary ? 148 : 112, 40), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.FromArgb(22, 39, 20) : Foreground,
                Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand, AccessibleName = text
            };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(60, 70, 78);
            return button;
        }
    }
}
