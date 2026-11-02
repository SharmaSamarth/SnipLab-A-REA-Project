using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipLab
{
    // Flyout content for the pen, highlighter and shapes tools: shape kind,
    // colour and thickness. Edits the InkOptions object directly.
    internal sealed class InkOptionsPanel : FlowLayoutPanel
    {
        private readonly InkOptions options;

        public event EventHandler Changed;

        public InkOptionsPanel(InkOptions options, Color[] palette, float[] sizes, bool showShapes)
        {
            this.options = options;
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(Theme.Px(8));
            BackColor = Theme.Surface;

            if (showShapes)
            {
                AddGroup("Shape", (ShapeKind[])Enum.GetValues(typeof(ShapeKind)), 4,
                    kind => (g, r) => ShapeIcons.Draw(g, kind, r, Theme.Text),
                    kind => kind.ToString(),
                    kind => options.Shape == kind,
                    kind => options.Shape = kind);
            }

            AddGroup("Color", palette, 5,
                color => (g, r) =>
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(color)) g.FillEllipse(brush, r);
                    using (var pen = new Pen(Theme.Border)) g.DrawEllipse(pen, r);
                },
                color => ColorName(color),
                color => options.Color.ToArgb() == color.ToArgb(),
                color => options.Color = color);

            float largest = sizes[sizes.Length - 1];
            AddGroup("Size", sizes, 5,
                size => (g, r) =>
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float d = Math.Max(Theme.Px(3), r.Width * size / largest);
                    using (var brush = new SolidBrush(Theme.Text))
                    {
                        g.FillEllipse(brush, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    }
                },
                size => size + " px",
                size => Math.Abs(options.Width - size) < 0.01f,
                size => options.Width = size);

            Size = PreferredSize;
        }

        private void AddGroup<T>(string caption, T[] values, int perRow,
            Func<T, Action<Graphics, Rectangle>> painter, Func<T, string> name,
            Func<T, bool> isSelected, Action<T> select)
        {
            Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                Font = Theme.UiFont,
                ForeColor = Theme.TextMuted,
                Margin = new Padding(Theme.Px(2), Theme.Px(4), 0, Theme.Px(2))
            });

            var chips = new List<OptionChip>();
            FlowLayoutPanel row = null;
            for (int i = 0; i < values.Length; i++)
            {
                if (i % perRow == 0)
                {
                    row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Surface };
                    Controls.Add(row);
                }
                T value = values[i];
                var chip = new OptionChip(painter(value), name(value)) { Selected = isSelected(value) };
                chip.Click += delegate
                {
                    select(value);
                    foreach (OptionChip other in chips) other.Selected = other == chip;
                    if (Changed != null) Changed(this, EventArgs.Empty);
                };
                chips.Add(chip);
                row.Controls.Add(chip);
            }
        }

        private static string ColorName(Color color)
        {
            Color known = Color.FromArgb(color.ToArgb());
            return known.IsNamedColor ? known.Name : "#" + (color.ToArgb() & 0xFFFFFF).ToString("X6");
        }
    }
}
