using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace GameBoost
{
    internal sealed class ResourceTile : Control
    {
        private const int HistoryLength = 60;
        private static readonly Color Ink = Color.FromArgb(24, 37, 61);
        private static readonly Color Muted = Color.FromArgb(77, 95, 119);
        private static readonly Color Border = Color.FromArgb(219, 228, 240);
        private const TextFormatFlags LabelFlags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        private readonly string title;
        private readonly Color accent;
        private readonly double?[] history = new double?[HistoryLength];
        private int nextSample;
        private int sampleCount;
        private string valueText = "—";
        private string detail = String.Empty;

        internal ResourceTile(string title, Color accent)
        {
            this.title = title;
            this.accent = accent;
            AccessibleName = title;
            AccessibleRole = AccessibleRole.Indicator;
            BackColor = Color.White;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        internal void UpdateValue(double? percent, string detail)
        {
            if (percent.HasValue && (Double.IsNaN(percent.Value) || percent.Value < 0 || percent.Value > 100))
                percent = null;

            history[nextSample] = percent;
            nextSample = (nextSample + 1) % HistoryLength;
            sampleCount = Math.Min(sampleCount + 1, HistoryLength);

            string previous = valueText;
            valueText = percent.HasValue ? percent.Value.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "—";
            this.detail = detail ?? String.Empty;
            AccessibleDescription = this.detail;
            if (IsHandleCreated && previous != valueText)
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TileAccessibleObject(this);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);

            Graphics graphics = args.Graphics;
            float scale = graphics.DpiX / 96F;
            int padding = Pixels(13, scale);
            int graphWidth = Math.Min(Width * 2 / 5, Pixels(112, scale));
            bool showGraph = Width >= Pixels(192, scale) && Height >= Pixels(53, scale);
            bool highContrast = SystemInformation.HighContrast;
            Color foreground = highContrast ? SystemColors.WindowText : Ink;
            Color secondary = highContrast ? SystemColors.WindowText : Muted;

            if (highContrast)
                graphics.Clear(SystemColors.Window);
            ControlPaint.DrawBorder(graphics, ClientRectangle, highContrast ? SystemColors.WindowText : Border, ButtonBorderStyle.Solid);

            int textWidth = Math.Max(0, Width - 2 * padding - (showGraph ? graphWidth + padding : 0));
            int textTop = Math.Max(Pixels(4, scale), (Height - Pixels(74, scale)) / 2);
            using (var labelFont = new Font(Font.FontFamily, 9.5F, FontStyle.Bold))
            using (var valueFont = new Font(Font.FontFamily, 22F, FontStyle.Bold))
            using (var detailFont = new Font(Font.FontFamily, 8.5F))
            {
                TextRenderer.DrawText(graphics, title, labelFont,
                    new Rectangle(padding, textTop, textWidth, Pixels(19, scale)), foreground, LabelFlags);
                TextRenderer.DrawText(graphics, valueText, valueFont,
                    new Rectangle(padding, textTop + Pixels(18, scale), textWidth, Pixels(36, scale)), foreground, LabelFlags);
                if (Height >= textTop + Pixels(70, scale))
                {
                    TextRenderer.DrawText(graphics, detail, detailFont,
                        new Rectangle(padding, textTop + Pixels(55, scale), textWidth, Pixels(17, scale)), secondary, LabelFlags);
                }
            }

            if (showGraph)
            {
                var graph = new Rectangle(Width - padding - graphWidth, Pixels(17, scale), graphWidth, Height - Pixels(34, scale));
                DrawHistory(graphics, graph, scale, highContrast);
            }
        }

        private void DrawHistory(Graphics graphics, Rectangle graph, float scale, bool highContrast)
        {
            Color line = highContrast ? SystemColors.WindowText : accent;
            Color grid = highContrast ? SystemColors.GrayText : Border;
            using (var gridPen = new Pen(grid))
            {
                for (int row = 0; row <= 2; row++)
                {
                    float y = graph.Top + graph.Height * row / 2F;
                    graphics.DrawLine(gridPen, graph.Left, y, graph.Right, y);
                }
            }

            SmoothingMode originalMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var pen = new Pen(line, Math.Max(1F, 1.75F * scale)))
                using (var brush = new SolidBrush(line))
                {
                    PointF? previous = null;
                    int oldest = (nextSample - sampleCount + HistoryLength) % HistoryLength;
                    for (int index = 0; index < sampleCount; index++)
                    {
                        double? percent = history[(oldest + index) % HistoryLength];
                        if (!percent.HasValue)
                        {
                            previous = null;
                            continue;
                        }

                        int slot = HistoryLength - sampleCount + index;
                        float x = graph.Left + graph.Width * slot / (float)(HistoryLength - 1);
                        float y = graph.Bottom - graph.Height * (float)(percent.Value / 100.0);
                        var point = new PointF(x, y);
                        if (previous.HasValue)
                            graphics.DrawLine(pen, previous.Value, point);
                        if (!previous.HasValue || index == sampleCount - 1)
                            graphics.FillEllipse(brush, x - 1.5F * scale, y - 1.5F * scale, 3F * scale, 3F * scale);
                        previous = point;
                    }
                }
            }
            finally
            {
                graphics.SmoothingMode = originalMode;
            }
        }

        private static int Pixels(int length, float scale)
        {
            return (int)Math.Round(length * scale);
        }

        private sealed class TileAccessibleObject : ControlAccessibleObject
        {
            private readonly ResourceTile tile;

            public TileAccessibleObject(ResourceTile tile) : base(tile)
            {
                this.tile = tile;
            }

            public override string Value { get { return tile.valueText; } }
        }
    }
}
