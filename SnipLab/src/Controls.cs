using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipLab
{
    // Flat toolbar button: an icon-font glyph (or custom-drawn icon), optional
    // label and chevron, rounded hover/pressed background, and a coloured bar
    // under the icon when checked.
    internal sealed class IconButton : Control
    {
        private bool hover;
        private bool pressed;
        private bool isChecked;
        private string glyph;
        private string label;
        private bool showChevron;
        private Color indicatorColor = Theme.Accent;

        public IconButton(string glyph, string toolTip)
        {
            this.glyph = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            Margin = new Padding(Theme.Px(1));
            AccessibleName = toolTip;
            AccessibleRole = AccessibleRole.PushButton;
            Theme.ToolTips.SetToolTip(this, toolTip);
            UpdateSize();
        }

        public Action<Graphics, Rectangle, Color> Painter { get; set; }

        public string Glyph
        {
            get { return glyph; }
            set { glyph = value; Invalidate(); }
        }

        public string Label
        {
            get { return label; }
            set { label = value; UpdateSize(); Invalidate(); }
        }

        public bool ShowChevron
        {
            get { return showChevron; }
            set { showChevron = value; UpdateSize(); Invalidate(); }
        }

        public bool Checked
        {
            get { return isChecked; }
            set { isChecked = value; Invalidate(); }
        }

        public Color IndicatorColor
        {
            get { return indicatorColor; }
            set { indicatorColor = value; Invalidate(); }
        }

        private void UpdateSize()
        {
            int height = Theme.Px(36);
            int width = height;
            if (!string.IsNullOrEmpty(label))
            {
                width += TextRenderer.MeasureText(label, Theme.UiFont).Width + Theme.Px(4);
            }
            if (showChevron)
            {
                width += Theme.Px(14);
            }
            Size = new Size(width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color back = Color.Empty;
            if (Enabled)
            {
                if (pressed) back = Theme.Pressed;
                else if (hover) back = Theme.Hover;
                else if (isChecked) back = Theme.Selected;
            }
            if (!back.IsEmpty)
            {
                Theme.FillRounded(g, back, new RectangleF(Theme.Px(2), Theme.Px(2), Width - Theme.Px(4), Height - Theme.Px(4)), Theme.Px(4));
            }

            Color fore = Enabled ? Theme.Text : Theme.Disabled;
            var icon = new Rectangle(0, 0, Height, Height);
            if (Painter != null)
            {
                int inset = Theme.Px(10);
                Painter(g, Rectangle.Inflate(icon, -inset, -inset), fore);
            }
            else
            {
                Theme.DrawGlyph(g, glyph, icon, fore, 12f);
            }

            int x = icon.Right;
            if (!string.IsNullOrEmpty(label))
            {
                var labelBounds = new Rectangle(x - Theme.Px(4), 0, Width - x, Height);
                TextRenderer.DrawText(g, label, Theme.UiFont, labelBounds, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            if (showChevron)
            {
                Theme.DrawGlyph(g, Glyphs.ChevronDown, new Rectangle(Width - Theme.Px(20), 0, Theme.Px(14), Height), fore, 7f);
            }

            if (isChecked && Enabled)
            {
                Theme.FillRounded(g, indicatorColor,
                    new RectangleF(icon.X + Theme.Px(12), Height - Theme.Px(6), icon.Width - Theme.Px(24), Theme.Px(3)), Theme.Px(1));
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }
    }

    internal sealed class ToolbarSeparator : Control
    {
        public ToolbarSeparator()
        {
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(Theme.Px(9), Theme.Px(36));
            Margin = Padding.Empty;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            using (var pen = new Pen(Theme.Border))
            {
                int x = Width / 2;
                e.Graphics.DrawLine(pen, x, Theme.Px(8), x, Height - Theme.Px(8));
            }
        }
    }

    internal sealed class ToolbarPanel : FlowLayoutPanel
    {
        public ToolbarPanel()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            WrapContents = false;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            BackColor = Theme.Background;
        }

        public void Add(params Control[] controls)
        {
            Controls.AddRange(controls);
        }
    }

    // Popup that hosts any control and closes when the user clicks elsewhere.
    internal sealed class Flyout : ToolStripDropDown
    {
        public Flyout(Control content)
        {
            Padding = Padding.Empty;
            BackColor = Theme.Surface;
            Renderer = Theme.CreateMenu().Renderer;
            content.BackColor = Theme.Surface;
            var host = new ToolStripControlHost(content)
            {
                AutoSize = false,
                Size = content.Size,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            Items.Add(host);
        }

        public void ShowBelow(Control anchor)
        {
            Show(anchor, new Point(0, anchor.Height + Theme.Px(2)));
        }
    }

    // Colour swatch or size dot inside a flyout.
    internal sealed class OptionChip : Control
    {
        private readonly Action<Graphics, Rectangle> paintContent;
        private bool hover;
        private bool selected;

        public OptionChip(Action<Graphics, Rectangle> paintContent, string toolTip)
        {
            this.paintContent = paintContent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(Theme.Px(32), Theme.Px(32));
            Margin = new Padding(Theme.Px(2));
            AccessibleName = toolTip;
            Theme.ToolTips.SetToolTip(this, toolTip);
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(Theme.Px(1), Theme.Px(1), Width - Theme.Px(3), Height - Theme.Px(3));
            if (hover && !selected)
            {
                Theme.FillRounded(g, Theme.Hover, bounds, Theme.Px(4));
            }
            if (selected)
            {
                using (var path = Theme.RoundedRect(bounds, Theme.Px(4)))
                using (var pen = new Pen(Theme.Accent, Theme.Px(2)))
                {
                    g.DrawPath(pen, path);
                }
            }
            int inset = Theme.Px(6);
            paintContent(g, new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }
    }

    // A short message that appears over the bottom of the window and fades out.
    internal sealed class Toast : Label
    {
        private readonly Timer hideTimer = new Timer { Interval = 2500 };

        public Toast()
        {
            AutoSize = true;
            Visible = false;
            Font = Theme.UiFont;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Padding = new Padding(Theme.Px(12), Theme.Px(8), Theme.Px(12), Theme.Px(8));
            BorderStyle = BorderStyle.FixedSingle;
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                Visible = false;
            };
        }

        public void ShowMessage(string message)
        {
            Text = message;
            Visible = true;
            BringToFront();
            if (Parent != null)
            {
                Location = new Point((Parent.ClientSize.Width - Width) / 2, Parent.ClientSize.Height - Height - Theme.Px(20));
            }
            hideTimer.Stop();
            hideTimer.Start();
        }
    }
}
