using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace SnipLab
{
    internal enum ShapeKind
    {
        Arrow,
        Line,
        Rectangle,
        Oval
    }

    // Something drawn on top of the snip. Finished annotations are never changed
    // in place, so undo snapshots can share them safely.
    internal abstract class Annotation
    {
        public abstract void Draw(Graphics g);

        public abstract bool HitTest(PointF point);

        public abstract Annotation Offset(float dx, float dy);

        protected static bool OutlineHit(GraphicsPath path, PointF point, float width)
        {
            using (var pen = new Pen(Color.Black, Math.Max(width, 10f)))
            {
                return path.IsOutlineVisible(point, pen);
            }
        }

        protected static Pen CreatePen(Color color, float width)
        {
            return new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
        }
    }

    internal sealed class InkStroke : Annotation
    {
        private readonly List<PointF> points;
        private readonly Color color;
        private readonly float width;

        public InkStroke(Color color, float width, IEnumerable<PointF> points)
        {
            this.color = color;
            this.width = width;
            this.points = points.ToList();
        }

        // Only used while the stroke is being drawn, before anything snapshots it.
        public void AddPoint(PointF point)
        {
            points.Add(point);
        }

        public override void Draw(Graphics g)
        {
            if (points.Count == 1)
            {
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, points[0].X - width / 2, points[0].Y - width / 2, width, width);
                }
                return;
            }
            using (Pen pen = CreatePen(color, width))
            {
                g.DrawLines(pen, points.ToArray());
            }
        }

        public override bool HitTest(PointF point)
        {
            if (points.Count == 1)
            {
                float dx = point.X - points[0].X, dy = point.Y - points[0].Y;
                return dx * dx + dy * dy <= Math.Max(width, 10f) * Math.Max(width, 10f);
            }
            using (var path = new GraphicsPath())
            {
                path.AddLines(points.ToArray());
                return OutlineHit(path, point, width);
            }
        }

        public override Annotation Offset(float dx, float dy)
        {
            return new InkStroke(color, width, points.Select(p => new PointF(p.X + dx, p.Y + dy)));
        }
    }

    internal sealed class ShapeAnnotation : Annotation
    {
        private readonly ShapeKind kind;
        private readonly PointF start;
        private readonly PointF end;
        private readonly Color color;
        private readonly float width;

        public ShapeAnnotation(ShapeKind kind, PointF start, PointF end, Color color, float width)
        {
            this.kind = kind;
            this.start = start;
            this.end = end;
            this.color = color;
            this.width = width;
        }

        public ShapeAnnotation WithEnd(PointF newEnd)
        {
            return new ShapeAnnotation(kind, start, newEnd, color, width);
        }

        private RectangleF Bounds
        {
            get
            {
                return RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                    Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
            }
        }

        private GraphicsPath CreatePath()
        {
            var path = new GraphicsPath();
            switch (kind)
            {
                case ShapeKind.Rectangle: path.AddRectangle(Bounds); break;
                case ShapeKind.Oval: path.AddEllipse(Bounds); break;
                default: path.AddLine(start, end); break;
            }
            return path;
        }

        public override void Draw(Graphics g)
        {
            using (GraphicsPath path = CreatePath())
            using (Pen pen = CreatePen(color, width))
            {
                if (kind == ShapeKind.Arrow)
                {
                    pen.CustomEndCap = new AdjustableArrowCap(3.5f, 3.5f, true);
                }
                g.DrawPath(pen, path);
            }
        }

        public override bool HitTest(PointF point)
        {
            using (GraphicsPath path = CreatePath())
            {
                return OutlineHit(path, point, width);
            }
        }

        public override Annotation Offset(float dx, float dy)
        {
            return new ShapeAnnotation(kind, new PointF(start.X + dx, start.Y + dy), new PointF(end.X + dx, end.Y + dy), color, width);
        }
    }

    // Solid block hiding sensitive text found by OCR (Quick redact).
    internal sealed class Redaction : Annotation
    {
        private readonly RectangleF bounds;

        public Redaction(RectangleF bounds)
        {
            this.bounds = bounds;
        }

        public override void Draw(Graphics g)
        {
            g.FillRectangle(Brushes.Black, bounds);
        }

        public override bool HitTest(PointF point)
        {
            return bounds.Contains(point);
        }

        public override Annotation Offset(float dx, float dy)
        {
            return new Redaction(new RectangleF(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height));
        }
    }

    // Icons for the shape kinds; Segoe Fluent Icons has no matching set.
    internal static class ShapeIcons
    {
        public static void Draw(Graphics g, ShapeKind kind, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, Math.Max(1.5f, r.Width / 10f)))
            {
                switch (kind)
                {
                    case ShapeKind.Arrow:
                        pen.CustomEndCap = new AdjustableArrowCap(3f, 3f, true);
                        g.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Top);
                        break;
                    case ShapeKind.Line:
                        g.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Top);
                        break;
                    case ShapeKind.Rectangle:
                        g.DrawRectangle(pen, r.X, r.Y + r.Height / 6, r.Width, r.Height * 2 / 3);
                        break;
                    case ShapeKind.Oval:
                        g.DrawEllipse(pen, r.X, r.Y + r.Height / 6, r.Width, r.Height * 2 / 3);
                        break;
                }
            }
        }

        // Toolbar icon for the Shapes tool: a square overlapping a circle.
        public static void DrawToolIcon(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, Math.Max(1.5f, r.Width / 12f)))
            {
                float s = r.Width * 0.62f;
                g.DrawRectangle(pen, r.X, r.Y, s, s);
                g.DrawEllipse(pen, r.Right - s, r.Bottom - s, s, s);
            }
        }
    }
}
