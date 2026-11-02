using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnipLab
{
    internal enum SnipMode
    {
        Rectangle,
        Window,
        FullScreen,
        Freeform
    }

    // Full-screen layer over a frozen copy of the desktop: the screen is captured
    // first, then the user picks a region of that still image. A floating bar at
    // the top switches between snip modes without leaving the overlay.
    internal sealed class OverlayForm : Form
    {
        public static readonly SnipMode[] Modes = { SnipMode.Rectangle, SnipMode.Window, SnipMode.FullScreen, SnipMode.Freeform };

        private readonly Bitmap screen;
        private readonly Bitmap dimmed;
        private readonly Rectangle virtualBounds;
        private readonly List<Rectangle> windows;
        private readonly List<Point> freeform = new List<Point>();
        private readonly Dictionary<SnipMode, IconButton> modeButtons = new Dictionary<SnipMode, IconButton>();
        private readonly ToolbarPanel modeBar = new ToolbarPanel();
        private readonly bool recording;
        private SnipMode mode;
        private Point dragStart;
        private bool dragging;
        private Rectangle highlight; // client coordinates

        // The snip (screenshot mode only).
        public Bitmap Result { get; private set; }

        // The chosen area in screen coordinates (recording mode uses this instead of Result).
        public Rectangle Selection { get; private set; }

        public SnipMode Mode
        {
            get { return mode; }
        }

        // In recording mode freeform is not offered: video frames are rectangles.
        public OverlayForm(Bitmap screen, Rectangle virtualBounds, SnipMode mode, List<Rectangle> windows, bool recording)
        {
            this.screen = screen;
            this.virtualBounds = virtualBounds;
            this.recording = recording;
            this.mode = recording && mode == SnipMode.Freeform ? SnipMode.Rectangle : mode;
            this.windows = windows;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = virtualBounds;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;

            dimmed = new Bitmap(screen);
            using (var g = Graphics.FromImage(dimmed))
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                g.FillRectangle(shade, 0, 0, dimmed.Width, dimmed.Height);
            }

            BuildModeBar();
            SetMode(this.mode);
        }

        public static string ModeName(SnipMode mode)
        {
            switch (mode)
            {
                case SnipMode.Window: return "Window";
                case SnipMode.FullScreen: return "Full screen";
                case SnipMode.Freeform: return "Freeform";
                default: return "Rectangle";
            }
        }

        private void BuildModeBar()
        {
            modeBar.BackColor = Theme.Surface;
            modeBar.Padding = new Padding(Theme.Px(4));
            modeBar.Cursor = Cursors.Default;
            foreach (SnipMode m in Modes)
            {
                if (recording && m == SnipMode.Freeform) continue;
                var button = new IconButton(Glyphs.ForMode(m), ModeName(m) + " mode");
                button.Click += delegate { SetMode(m); };
                modeButtons[m] = button;
                modeBar.Add(button);
            }
            var close = new IconButton(Glyphs.Close, "Close");
            close.Click += delegate { DialogResult = DialogResult.Cancel; };
            modeBar.Add(new ToolbarSeparator(), close);
            modeBar.Paint += (sender, e) =>
            {
                using (var pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, modeBar.Width - 1, modeBar.Height - 1);
                }
            };
            Controls.Add(modeBar);
        }

        private void SetMode(SnipMode newMode)
        {
            mode = newMode;
            foreach (KeyValuePair<SnipMode, IconButton> pair in modeButtons)
            {
                pair.Value.Checked = pair.Key == mode;
            }
            dragging = false;
            highlight = Rectangle.Empty;
            freeform.Clear();
            if (IsHandleCreated && (mode == SnipMode.Window || mode == SnipMode.FullScreen))
            {
                UpdateHover(PointToClient(Cursor.Position));
            }
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = virtualBounds;

            // Centre the mode bar at the top of the monitor the pointer is on.
            Rectangle monitor = Screen.FromPoint(Cursor.Position).Bounds;
            modeBar.Location = new Point(
                monitor.X - virtualBounds.X + (monitor.Width - modeBar.Width) / 2,
                monitor.Y - virtualBounds.Y + Theme.Px(16));

            Activate();
            if (mode == SnipMode.Window || mode == SnipMode.FullScreen)
            {
                UpdateHover(PointToClient(Cursor.Position));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            var full = new Rectangle(Point.Empty, screen.Size);
            g.DrawImage(dimmed, full);

            if (mode == SnipMode.Freeform)
            {
                if (freeform.Count > 1)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddLines(freeform.ToArray());
                        GraphicsState state = g.Save();
                        g.SetClip(path);
                        g.DrawImage(screen, full);
                        g.Restore(state);
                    }
                    using (var pen = new Pen(Color.White, 2))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawLines(pen, freeform.ToArray());
                    }
                }
            }
            else if (!highlight.IsEmpty)
            {
                g.DrawImage(screen, highlight, highlight, GraphicsUnit.Pixel);
                using (var pen = new Pen(Theme.Accent, 2))
                {
                    g.DrawRectangle(pen, highlight);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                DialogResult = DialogResult.Cancel;
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            switch (mode)
            {
                case SnipMode.Rectangle:
                    dragging = true;
                    dragStart = e.Location;
                    highlight = Rectangle.Empty;
                    break;
                case SnipMode.Freeform:
                    dragging = true;
                    freeform.Clear();
                    freeform.Add(e.Location);
                    break;
                default:
                    UpdateHover(e.Location);
                    if (!highlight.IsEmpty) FinishRegion(highlight);
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            switch (mode)
            {
                case SnipMode.Rectangle:
                    if (!dragging) return;
                    highlight = Rectangle.FromLTRB(
                        Math.Min(dragStart.X, e.X), Math.Min(dragStart.Y, e.Y),
                        Math.Max(dragStart.X, e.X), Math.Max(dragStart.Y, e.Y));
                    Invalidate();
                    break;
                case SnipMode.Freeform:
                    if (!dragging) return;
                    freeform.Add(e.Location);
                    Invalidate();
                    break;
                default:
                    UpdateHover(e.Location);
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;

            if (mode == SnipMode.Rectangle && highlight.Width > 2 && highlight.Height > 2)
            {
                FinishRegion(highlight);
            }
            else if (mode == SnipMode.Freeform && freeform.Count > 2)
            {
                Bitmap snip = CopyFreeform();
                if (snip != null) Finish(snip);
            }
            else
            {
                highlight = Rectangle.Empty;
                freeform.Clear();
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        }

        // Window mode: topmost window under the pointer. Full-screen mode: monitor under the pointer.
        private void UpdateHover(Point client)
        {
            var screenPoint = new Point(client.X + virtualBounds.X, client.Y + virtualBounds.Y);
            Rectangle target = Rectangle.Empty;
            if (mode == SnipMode.Window)
            {
                foreach (Rectangle window in windows)
                {
                    if (window.Contains(screenPoint))
                    {
                        target = window;
                        break;
                    }
                }
            }
            else
            {
                target = Screen.FromPoint(screenPoint).Bounds;
            }

            if (!target.IsEmpty)
            {
                target.Offset(-virtualBounds.X, -virtualBounds.Y);
                target.Intersect(new Rectangle(Point.Empty, screen.Size));
            }
            if (target != highlight)
            {
                highlight = target;
                Invalidate();
            }
        }

        private Bitmap CopyRegion(Rectangle region)
        {
            var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(screen, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
            }
            return bitmap;
        }

        // Keeps only the pixels inside the drawn shape; the rest stays transparent.
        private Bitmap CopyFreeform()
        {
            using (var path = new GraphicsPath())
            {
                path.AddLines(freeform.ToArray());
                path.CloseFigure();
                Rectangle bounds = Rectangle.Round(path.GetBounds());
                bounds.Intersect(new Rectangle(Point.Empty, screen.Size));
                if (bounds.Width < 2 || bounds.Height < 2) return null;

                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.TranslateTransform(-bounds.X, -bounds.Y);
                    g.SetClip(path);
                    g.DrawImage(screen, new Rectangle(Point.Empty, screen.Size));
                }
                return bitmap;
            }
        }

        private void FinishRegion(Rectangle client)
        {
            Selection = new Rectangle(client.X + virtualBounds.X, client.Y + virtualBounds.Y, client.Width, client.Height);
            if (!recording) Result = CopyRegion(client);
            DialogResult = DialogResult.OK;
        }

        private void Finish(Bitmap snip)
        {
            Result = snip;
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) dimmed.Dispose();
            base.Dispose(disposing);
        }
    }
}
