using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SnipLab
{
    internal enum CanvasTool
    {
        Pen,
        Highlighter,
        Eraser,
        Shape,
        Crop,
        Text
    }

    internal sealed class InkOptions
    {
        public Color Color;
        public float Width;
        public ShapeKind Shape;

        public InkOptions(Color color, float width)
        {
            Color = color;
            Width = width;
        }
    }

    // The editing surface: shows the snip at any zoom and handles drawing,
    // erasing, shapes, crop handles and OCR text selection. All coordinates
    // stored here are image pixels; only painting and mouse input apply the zoom.
    internal sealed class InkCanvas : Control
    {
        private enum CropHandle { None, Move, N, S, E, W, NE, NW, SE, SW }

        private sealed class Snapshot
        {
            public readonly Bitmap Image;
            public readonly List<Annotation> Items;

            public Snapshot(Bitmap image, List<Annotation> items)
            {
                Image = image;
                Items = items;
            }
        }

        private const float MinCropSize = 8f;
        private static readonly Regex EmailPattern = new Regex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+");
        private static readonly Regex PhonePattern = new Regex(@"\+?\d[\d\s().-]{6,}\d");

        public readonly InkOptions PenOptions = new InkOptions(Color.FromArgb(232, 17, 35), 4f);
        public readonly InkOptions HighlighterOptions = new InkOptions(Color.FromArgb(255, 235, 0), 16f);
        public readonly InkOptions ShapeOptions = new InkOptions(Color.FromArgb(232, 17, 35), 4f);

        private readonly Stack<Snapshot> undo = new Stack<Snapshot>();
        private readonly Stack<Snapshot> redo = new Stack<Snapshot>();
        private readonly List<Bitmap> ownedImages = new List<Bitmap>();
        private readonly HashSet<RecognizedWord> selectedWords = new HashSet<RecognizedWord>();
        private Bitmap image;
        private List<Annotation> items = new List<Annotation>();
        private List<RecognizedLine> textLines;
        private CanvasTool tool = CanvasTool.Pen;
        private float zoom = 1f;

        // Gesture in progress.
        private InkStroke drawingStroke;
        private ShapeAnnotation drawingShape;
        private bool erasing;
        private bool erasedThisDrag;
        private bool selectingText;
        private PointF dragStart;
        private RectangleF cropRect;
        private RectangleF cropAtDragStart;
        private CropHandle cropHandle;

        public event EventHandler StateChanged;
        public event EventHandler ZoomChanged;

        public InkCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.CanvasBack;
            Cursor = Cursors.Cross;
        }

        public bool HasImage { get { return image != null; } }
        public Bitmap BaseImage { get { return image; } }
        public Size ImageSize { get { return image == null ? Size.Empty : image.Size; } }
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        public bool HasTextResult { get { return textLines != null; } }
        public int TextLineCount { get { return textLines == null ? 0 : textLines.Count; } }
        public bool HasTextSelection { get { return selectedWords.Count > 0; } }

        public CanvasTool Tool
        {
            get { return tool; }
            set
            {
                if (tool == value) return;
                tool = value;
                if (tool == CanvasTool.Crop && image != null)
                {
                    cropRect = new RectangleF(PointF.Empty, image.Size);
                }
                selectedWords.Clear();
                Cursor = tool == CanvasTool.Text ? Cursors.IBeam : tool == CanvasTool.Crop ? Cursors.Default : Cursors.Cross;
                Invalidate();
                Raise(StateChanged);
            }
        }

        public float Zoom
        {
            get { return zoom; }
            set
            {
                float clamped = Math.Max(0.1f, Math.Min(8f, value));
                if (Math.Abs(clamped - zoom) < 0.0001f) return;
                zoom = clamped;
                UpdateSize();
                Invalidate();
                Raise(ZoomChanged);
            }
        }

        public void SetImage(Bitmap bitmap)
        {
            foreach (Bitmap owned in ownedImages) owned.Dispose();
            ownedImages.Clear();
            undo.Clear();
            redo.Clear();
            image = bitmap;
            ownedImages.Add(bitmap);
            items = new List<Annotation>();
            textLines = null;
            selectedWords.Clear();
            if (tool == CanvasTool.Crop || tool == CanvasTool.Text)
            {
                tool = CanvasTool.Pen;
                Cursor = Cursors.Cross;
            }
            UpdateSize();
            Invalidate();
            Raise(StateChanged);
        }

        public Bitmap Render()
        {
            var result = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (Annotation item in items) item.Draw(g);
            }
            return result;
        }

        public void Undo()
        {
            if (!CanUndo) return;
            redo.Push(CurrentState());
            Restore(undo.Pop());
        }

        public void Redo()
        {
            if (!CanRedo) return;
            undo.Push(CurrentState());
            Restore(redo.Pop());
        }

        public void EraseAllInk()
        {
            if (!items.Any(i => !(i is Redaction))) return;
            Commit();
            items.RemoveAll(i => !(i is Redaction));
            Invalidate();
            Raise(StateChanged);
        }

        public void ApplyCrop()
        {
            Rectangle r = Rectangle.Round(cropRect);
            r.Intersect(new Rectangle(Point.Empty, image.Size));
            if (r.Width < 1 || r.Height < 1 || r.Size == image.Size) return;

            Commit();
            var cropped = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(image, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            }
            ownedImages.Add(cropped);
            image = cropped;
            items = items.Select(i => i.Offset(-r.X, -r.Y)).ToList();
            textLines = null;
            selectedWords.Clear();
            UpdateSize();
            Invalidate();
            Raise(StateChanged);
        }

        public void SetText(List<RecognizedLine> lines)
        {
            textLines = lines;
            selectedWords.Clear();
            Invalidate();
            Raise(StateChanged);
        }

        public string GetAllText()
        {
            return textLines == null ? string.Empty : TextRecognizer.JoinLines(textLines);
        }

        public string GetSelectedText()
        {
            var text = new StringBuilder();
            foreach (RecognizedLine line in textLines)
            {
                string words = string.Join(" ", line.Words.Where(selectedWords.Contains).Select(w => w.Text));
                if (words.Length == 0) continue;
                if (text.Length > 0) text.AppendLine();
                text.Append(words);
            }
            return text.ToString();
        }

        // Blacks out email addresses and phone numbers. Patterns run on whole lines
        // because OCR often splits a phone number into several words.
        public int RedactSensitiveText()
        {
            if (textLines == null) return 0;
            var targets = new HashSet<RecognizedWord>();
            foreach (RecognizedLine line in textLines)
            {
                var text = new StringBuilder();
                var starts = new List<int>();
                foreach (RecognizedWord word in line.Words)
                {
                    if (text.Length > 0) text.Append(' ');
                    starts.Add(text.Length);
                    text.Append(word.Text);
                }
                string lineText = text.ToString();
                foreach (Regex pattern in new[] { EmailPattern, PhonePattern })
                {
                    foreach (Match match in pattern.Matches(lineText))
                    {
                        for (int i = 0; i < line.Words.Count; i++)
                        {
                            int wordEnd = starts[i] + line.Words[i].Text.Length;
                            if (starts[i] < match.Index + match.Length && wordEnd > match.Index) targets.Add(line.Words[i]);
                        }
                    }
                }
            }
            if (targets.Count == 0) return 0;

            Commit();
            foreach (RecognizedWord word in targets)
            {
                items.Add(new Redaction(RectangleF.Inflate(word.Bounds, 2, 2)));
            }
            Invalidate();
            Raise(StateChanged);
            return targets.Count;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (image == null) return;

            g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.ScaleTransform(zoom, zoom);
            var bounds = new Rectangle(0, 0, image.Width, image.Height);
            g.FillRectangle(Brushes.White, bounds); // behind transparent freeform areas
            g.DrawImage(image, bounds);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Annotation item in items) item.Draw(g);
            if (tool == CanvasTool.Text) PaintText(g);
            if (tool == CanvasTool.Crop) PaintCrop(g);
        }

        private void PaintText(Graphics g)
        {
            if (textLines == null) return;
            using (var normal = new SolidBrush(Color.FromArgb(50, Theme.Accent)))
            using (var selected = new SolidBrush(Color.FromArgb(130, Theme.Accent)))
            {
                foreach (RecognizedWord word in textLines.SelectMany(l => l.Words))
                {
                    g.FillRectangle(selectedWords.Contains(word) ? selected : normal, RectangleF.Inflate(word.Bounds, 1, 1));
                }
            }
        }

        private void PaintCrop(Graphics g)
        {
            using (var outside = new Region(new RectangleF(PointF.Empty, image.Size)))
            using (var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            {
                outside.Exclude(cropRect);
                g.FillRegion(shade, outside);
            }
            using (var pen = new Pen(Color.White, 2f / zoom))
            {
                g.DrawRectangle(pen, cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
            }
            float size = Theme.Px(12) / zoom;
            using (var fill = new SolidBrush(Color.White))
            using (var outline = new Pen(Theme.Accent, 2f / zoom))
            {
                foreach (KeyValuePair<CropHandle, PointF> handle in CropHandles())
                {
                    var r = new RectangleF(handle.Value.X - size / 2, handle.Value.Y - size / 2, size, size);
                    g.FillEllipse(fill, r);
                    g.DrawEllipse(outline, r);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (image == null || e.Button != MouseButtons.Left) return;
            PointF p = ToImage(e.Location);
            dragStart = p;

            switch (tool)
            {
                case CanvasTool.Pen:
                case CanvasTool.Highlighter:
                    InkOptions ink = tool == CanvasTool.Pen ? PenOptions : HighlighterOptions;
                    Color color = tool == CanvasTool.Highlighter ? Color.FromArgb(110, ink.Color) : ink.Color;
                    Commit();
                    drawingStroke = new InkStroke(color, ink.Width, new[] { p });
                    items.Add(drawingStroke);
                    break;
                case CanvasTool.Shape:
                    Commit();
                    drawingShape = new ShapeAnnotation(ShapeOptions.Shape, p, p, ShapeOptions.Color, ShapeOptions.Width);
                    items.Add(drawingShape);
                    break;
                case CanvasTool.Eraser:
                    erasing = true;
                    erasedThisDrag = false;
                    EraseAt(p);
                    break;
                case CanvasTool.Crop:
                    cropHandle = HitCropHandle(p);
                    cropAtDragStart = cropRect;
                    break;
                case CanvasTool.Text:
                    selectingText = true;
                    if ((ModifierKeys & Keys.Shift) == 0) selectedWords.Clear();
                    SelectWords(p);
                    break;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (image == null) return;
            PointF p = ToImage(e.Location);

            if (drawingStroke != null)
            {
                drawingStroke.AddPoint(p);
            }
            else if (drawingShape != null)
            {
                drawingShape = drawingShape.WithEnd(p);
                items[items.Count - 1] = drawingShape;
            }
            else if (erasing)
            {
                EraseAt(p);
                return;
            }
            else if (tool == CanvasTool.Crop)
            {
                if (cropHandle == CropHandle.None)
                {
                    Cursor = CursorFor(HitCropHandle(p));
                    return;
                }
                DragCrop(p);
            }
            else if (selectingText)
            {
                SelectWords(p);
            }
            else
            {
                return;
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool changed = drawingStroke != null || drawingShape != null || erasedThisDrag || selectingText;
            drawingStroke = null;
            drawingShape = null;
            erasing = false;
            erasedThisDrag = false;
            selectingText = false;
            cropHandle = CropHandle.None;
            if (changed) Raise(StateChanged);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (image != null && (ModifierKeys & Keys.Control) != 0)
            {
                Zoom *= e.Delta > 0 ? 1.1f : 1 / 1.1f;
                var handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true; // stop the parent panel scrolling too
                return;
            }
            base.OnMouseWheel(e);
        }

        private PointF ToImage(Point client)
        {
            return new PointF(client.X / zoom, client.Y / zoom);
        }

        private void UpdateSize()
        {
            if (image == null) return;
            Size = new Size((int)Math.Ceiling(image.Width * zoom), (int)Math.Ceiling(image.Height * zoom));
        }

        // Called before every change so the previous state can be restored.
        private void Commit()
        {
            undo.Push(CurrentState());
            redo.Clear();
        }

        private Snapshot CurrentState()
        {
            return new Snapshot(image, new List<Annotation>(items));
        }

        private void Restore(Snapshot state)
        {
            bool imageChanged = state.Image != image;
            image = state.Image;
            items = new List<Annotation>(state.Items);
            if (imageChanged)
            {
                textLines = null; // word positions belong to the other image
                selectedWords.Clear();
                UpdateSize();
            }
            Invalidate();
            Raise(StateChanged);
        }

        private void EraseAt(PointF p)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is Redaction || !items[i].HitTest(p)) continue;
                if (!erasedThisDrag)
                {
                    Commit(); // one undo step per eraser drag
                    erasedThisDrag = true;
                }
                items.RemoveAt(i);
                Invalidate();
                return;
            }
        }

        private void SelectWords(PointF p)
        {
            if (textLines == null) return;
            RectangleF area = RectangleF.FromLTRB(Math.Min(dragStart.X, p.X), Math.Min(dragStart.Y, p.Y),
                Math.Max(dragStart.X, p.X) + 1, Math.Max(dragStart.Y, p.Y) + 1);
            foreach (RecognizedWord word in textLines.SelectMany(l => l.Words))
            {
                if (word.Bounds.IntersectsWith(area)) selectedWords.Add(word);
            }
        }

        private IEnumerable<KeyValuePair<CropHandle, PointF>> CropHandles()
        {
            RectangleF r = cropRect;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.NW, new PointF(r.Left, r.Top));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.N, new PointF(cx, r.Top));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.NE, new PointF(r.Right, r.Top));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.E, new PointF(r.Right, cy));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.SE, new PointF(r.Right, r.Bottom));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.S, new PointF(cx, r.Bottom));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.SW, new PointF(r.Left, r.Bottom));
            yield return new KeyValuePair<CropHandle, PointF>(CropHandle.W, new PointF(r.Left, cy));
        }

        private CropHandle HitCropHandle(PointF p)
        {
            float tolerance = Theme.Px(10) / zoom;
            foreach (KeyValuePair<CropHandle, PointF> handle in CropHandles())
            {
                if (Math.Abs(p.X - handle.Value.X) <= tolerance && Math.Abs(p.Y - handle.Value.Y) <= tolerance) return handle.Key;
            }
            return cropRect.Contains(p) ? CropHandle.Move : CropHandle.None;
        }

        private void DragCrop(PointF p)
        {
            float dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
            RectangleF r = cropAtDragStart;
            float left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            if (cropHandle == CropHandle.Move)
            {
                dx = Clamp(dx, -left, image.Width - right);
                dy = Clamp(dy, -top, image.Height - bottom);
                left += dx; right += dx; top += dy; bottom += dy;
            }
            else
            {
                if (cropHandle == CropHandle.W || cropHandle == CropHandle.NW || cropHandle == CropHandle.SW) left = Clamp(left + dx, 0, right - MinCropSize);
                if (cropHandle == CropHandle.E || cropHandle == CropHandle.NE || cropHandle == CropHandle.SE) right = Clamp(right + dx, left + MinCropSize, image.Width);
                if (cropHandle == CropHandle.N || cropHandle == CropHandle.NW || cropHandle == CropHandle.NE) top = Clamp(top + dy, 0, bottom - MinCropSize);
                if (cropHandle == CropHandle.S || cropHandle == CropHandle.SW || cropHandle == CropHandle.SE) bottom = Clamp(bottom + dy, top + MinCropSize, image.Height);
            }
            cropRect = RectangleF.FromLTRB(left, top, right, bottom);
        }

        private static Cursor CursorFor(CropHandle handle)
        {
            switch (handle)
            {
                case CropHandle.Move: return Cursors.SizeAll;
                case CropHandle.N: case CropHandle.S: return Cursors.SizeNS;
                case CropHandle.E: case CropHandle.W: return Cursors.SizeWE;
                case CropHandle.NE: case CropHandle.SW: return Cursors.SizeNESW;
                case CropHandle.NW: case CropHandle.SE: return Cursors.SizeNWSE;
                default: return Cursors.Default;
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Bitmap owned in ownedImages) owned.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
