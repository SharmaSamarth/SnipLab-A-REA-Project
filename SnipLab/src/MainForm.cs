using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SnipLab
{
    // Main window. Starts small with just the capture controls (New, snip/record,
    // mode, delay), then grows into an image editor after a snip or a video
    // preview after a recording — the same flow as Snipping Tool.
    internal sealed class MainForm : Form
    {
        private static readonly int[] Delays = { 0, 3, 5, 10 };
        private static readonly Color[] PenColors =
        {
            Color.Black, Color.White, Color.FromArgb(122, 117, 116), Color.FromArgb(232, 17, 35), Color.FromArgb(247, 99, 12),
            Color.FromArgb(255, 185, 0), Color.FromArgb(16, 124, 16), Color.FromArgb(0, 120, 212), Color.FromArgb(136, 108, 228), Color.FromArgb(227, 0, 140)
        };
        private static readonly Color[] HighlighterColors =
        {
            Color.FromArgb(255, 235, 0), Color.FromArgb(102, 255, 0), Color.FromArgb(0, 230, 255),
            Color.FromArgb(255, 64, 200), Color.FromArgb(255, 140, 0), Color.FromArgb(180, 120, 255)
        };
        private static readonly float[] PenSizes = { 2f, 4f, 8f, 12f, 16f };
        private static readonly float[] HighlighterSizes = { 8f, 12f, 16f, 24f, 32f };

        private readonly Settings settings = Settings.Load();
        private readonly InkCanvas canvas = new InkCanvas();
        private readonly VideoView videoView = new VideoView();
        private readonly Panel scroller = new Panel();
        private readonly Label emptyLabel = new Label();
        private readonly Toast toast = new Toast();
        private readonly Timer captureTimer = new Timer();
        private readonly TableLayoutPanel toolbar = new TableLayoutPanel();
        private readonly ToolbarPanel editTools = new ToolbarPanel();
        private readonly ToolbarPanel cropTools = new ToolbarPanel();
        private readonly ToolbarPanel viewTools = new ToolbarPanel();
        private readonly ToolbarPanel videoTools = new ToolbarPanel();
        private readonly ToolbarPanel textBar = new ToolbarPanel();
        private readonly Label textStatus = new Label();
        private readonly Label zoomLabel = new Label();

        private readonly IconButton snipKindButton = new IconButton(Glyphs.Camera, "Snip");
        private readonly IconButton recordKindButton = new IconButton(Glyphs.Video, "Record");
        private readonly IconButton modeButton = new IconButton(Glyphs.RectangleMode, "Snipping mode") { ShowChevron = true };
        private readonly IconButton delayButton = new IconButton(Glyphs.Clock, "Delay") { ShowChevron = true };
        private readonly IconButton penButton = new IconButton(Glyphs.Pen, "Ballpoint pen");
        private readonly IconButton highlighterButton = new IconButton(Glyphs.Highlighter, "Highlighter");
        private readonly IconButton eraserButton = new IconButton(Glyphs.Eraser, "Eraser");
        private readonly IconButton shapeButton = new IconButton(null, "Shapes") { Painter = ShapeIcons.DrawToolIcon };
        private readonly IconButton cropButton = new IconButton(Glyphs.Crop, "Crop");
        private readonly IconButton textButton = new IconButton(Glyphs.TextActions, "Text actions");
        private readonly IconButton undoButton = new IconButton(Glyphs.Undo, "Undo (Ctrl+Z)");
        private readonly IconButton redoButton = new IconButton(Glyphs.Redo, "Redo (Ctrl+Y)");

        private CanvasTool previousTool = CanvasTool.Pen;
        private RecordingResult video;

        public MainForm()
        {
            Text = "SnipLab";
            Font = Theme.UiFont;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.Px(720), Theme.Px(220));
            MinimumSize = new Size(Theme.Px(560), Theme.Px(200));
            using (Bitmap icon = Theme.GlyphImage(Glyphs.Snip, Theme.Accent)) Icon = Icon.FromHandle(icon.GetHicon());

            BuildToolbar();
            BuildTextBar();
            BuildBody();

            // Later-added controls dock first: toolbar on top, text bar under it, body fills the rest.
            Controls.Add(BodyPanel);
            Controls.Add(textBar);
            Controls.Add(toolbar);

            canvas.StateChanged += delegate { UpdateUi(); };
            canvas.ZoomChanged += delegate
            {
                zoomLabel.Text = (int)Math.Round(canvas.Zoom * 100) + "%";
                CenterCanvas();
            };
            captureTimer.Tick += delegate
            {
                captureTimer.Stop();
                if (settings.Record) SelectRecordingArea();
                else CaptureAndSelect();
            };

            UpdateModeButton();
            UpdateDelayButton();
            UpdateUi();
        }

        private Panel BodyPanel { get; set; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            settings.Save();
            base.OnFormClosed(e);
        }

        private void BuildToolbar()
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.AutoSize = true;
            toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            toolbar.BackColor = Theme.Background;
            toolbar.Padding = new Padding(Theme.Px(6), Theme.Px(4), Theme.Px(6), Theme.Px(4));
            toolbar.ColumnCount = 3;
            toolbar.RowCount = 1;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var newButton = new IconButton(Glyphs.Add, "New (Ctrl+N)") { Label = "New" };
            newButton.Click += delegate { StartSnip(); };
            snipKindButton.Click += delegate { SetRecordMode(false); };
            recordKindButton.Click += delegate { SetRecordMode(true); };
            modeButton.Click += delegate { ShowModeMenu(); };
            delayButton.Click += delegate { ShowDelayMenu(); };
            var left = new ToolbarPanel();
            left.Add(newButton, new ToolbarSeparator(), snipKindButton, recordKindButton, new ToolbarSeparator(), modeButton, delayButton);

            penButton.Click += delegate { ToolClicked(CanvasTool.Pen, penButton, () => new InkOptionsPanel(canvas.PenOptions, PenColors, PenSizes, false)); };
            highlighterButton.Click += delegate { ToolClicked(CanvasTool.Highlighter, highlighterButton, () => new InkOptionsPanel(canvas.HighlighterOptions, HighlighterColors, HighlighterSizes, false)); };
            shapeButton.Click += delegate { ToolClicked(CanvasTool.Shape, shapeButton, () => new InkOptionsPanel(canvas.ShapeOptions, PenColors, PenSizes, true)); };
            eraserButton.Click += delegate { EraserClicked(); };
            cropButton.Click += delegate { EnterCrop(); };
            textButton.Click += delegate { ToggleTextActions(); };
            undoButton.Click += delegate { canvas.Undo(); };
            redoButton.Click += delegate { canvas.Redo(); };
            editTools.Add(penButton, highlighterButton, eraserButton, shapeButton, cropButton, textButton,
                new ToolbarSeparator(), undoButton, redoButton);

            var applyCrop = new IconButton(Glyphs.Check, "Apply crop (Enter)") { Label = "Apply" };
            applyCrop.Click += delegate { ApplyCrop(); };
            var cancelCrop = new IconButton(Glyphs.Close, "Cancel crop (Esc)") { Label = "Cancel" };
            cancelCrop.Click += delegate { canvas.Tool = previousTool; };
            cropTools.Add(applyCrop, cancelCrop);

            var center = new ToolbarPanel { Anchor = AnchorStyles.None };
            center.Add(editTools, cropTools);

            var zoomOut = new IconButton(Glyphs.ZoomOut, "Zoom out (Ctrl+-)");
            zoomOut.Click += delegate { canvas.Zoom /= 1.25f; };
            var zoomIn = new IconButton(Glyphs.ZoomIn, "Zoom in (Ctrl++)");
            zoomIn.Click += delegate { canvas.Zoom *= 1.25f; };
            zoomLabel.AutoSize = false;
            zoomLabel.Size = new Size(Theme.Px(48), Theme.Px(36));
            zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
            zoomLabel.ForeColor = Theme.Text;
            zoomLabel.Cursor = Cursors.Hand;
            zoomLabel.Click += delegate { FitZoom(); };
            Theme.ToolTips.SetToolTip(zoomLabel, "Fit to window (Ctrl+0)");
            var save = new IconButton(Glyphs.Save, "Save as (Ctrl+S)");
            save.Click += delegate { SaveAs(); };
            var copy = new IconButton(Glyphs.Copy, "Copy (Ctrl+C)");
            copy.Click += delegate { Copy(); };
            viewTools.Add(zoomOut, zoomLabel, zoomIn, new ToolbarSeparator(), save, copy);

            var play = new IconButton(Glyphs.Play, "Play in your video player") { Label = "Play" };
            play.Click += delegate { PlayVideo(); };
            var saveVideo = new IconButton(Glyphs.Save, "Save as (Ctrl+S)");
            saveVideo.Click += delegate { SaveAs(); };
            var copyVideo = new IconButton(Glyphs.Copy, "Copy file (Ctrl+C)");
            copyVideo.Click += delegate { Copy(); };
            videoTools.Add(play, new ToolbarSeparator(), saveVideo, copyVideo);

            var more = new IconButton(Glyphs.More, "See more");
            more.Click += delegate { ShowMoreMenu(more); };
            var right = new ToolbarPanel();
            right.Add(viewTools, videoTools, more);

            toolbar.Controls.Add(left, 0, 0);
            toolbar.Controls.Add(center, 1, 0);
            toolbar.Controls.Add(right, 2, 0);
        }

        private void BuildTextBar()
        {
            textBar.Dock = DockStyle.Top;
            textBar.Padding = new Padding(Theme.Px(6), 0, Theme.Px(6), Theme.Px(4));
            var copyAll = new IconButton(Glyphs.Copy, "Copy all text") { Label = "Copy all text" };
            copyAll.Click += delegate { CopyAllText(); };
            var redact = new IconButton(null, "Hide email addresses and phone numbers")
            {
                Label = "Quick redact",
                Painter = (g, r, color) =>
                {
                    using (var brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, r.X, r.Y + r.Height / 4, r.Width, r.Height / 2);
                    }
                }
            };
            redact.Click += delegate { QuickRedact(); };
            textStatus.AutoSize = true;
            textStatus.ForeColor = Theme.TextMuted;
            textStatus.Margin = new Padding(Theme.Px(8), Theme.Px(10), 0, 0);
            textBar.Add(copyAll, redact, textStatus);
        }

        private void BuildBody()
        {
            BodyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CanvasBack };
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.BackColor = Theme.CanvasBack;
            scroller.Controls.Add(canvas);
            scroller.Resize += delegate { CenterCanvas(); };

            videoView.Dock = DockStyle.Fill;
            videoView.Click += delegate { PlayVideo(); };

            emptyLabel.Dock = DockStyle.Fill;
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            emptyLabel.ForeColor = Theme.TextMuted;

            BodyPanel.Controls.Add(toast);
            BodyPanel.Controls.Add(scroller);
            BodyPanel.Controls.Add(videoView);
            BodyPanel.Controls.Add(emptyLabel);
        }

        private void UpdateUi()
        {
            bool hasVideo = video != null;
            bool hasImage = canvas.HasImage && !hasVideo;
            CanvasTool tool = canvas.Tool;

            snipKindButton.Checked = !settings.Record;
            recordKindButton.Checked = settings.Record;
            editTools.Visible = hasImage && tool != CanvasTool.Crop;
            cropTools.Visible = hasImage && tool == CanvasTool.Crop;
            viewTools.Visible = hasImage;
            videoTools.Visible = hasVideo;
            textBar.Visible = hasImage && tool == CanvasTool.Text;
            scroller.Visible = hasImage;
            videoView.Visible = hasVideo;
            emptyLabel.Visible = !hasImage && !hasVideo;
            emptyLabel.Text = settings.Record
                ? "Select New or press Ctrl+N to start a screen recording."
                : "Press Ctrl+N or select New to start a snip.";

            penButton.Checked = tool == CanvasTool.Pen;
            penButton.IndicatorColor = canvas.PenOptions.Color;
            highlighterButton.Checked = tool == CanvasTool.Highlighter;
            highlighterButton.IndicatorColor = canvas.HighlighterOptions.Color;
            eraserButton.Checked = tool == CanvasTool.Eraser;
            shapeButton.Checked = tool == CanvasTool.Shape;
            shapeButton.IndicatorColor = canvas.ShapeOptions.Color;
            textButton.Checked = tool == CanvasTool.Text;
            undoButton.Enabled = canvas.CanUndo;
            redoButton.Enabled = canvas.CanRedo;

            textStatus.Text = canvas.TextLineCount == 0
                ? "No text found in this snip."
                : canvas.HasTextSelection
                    ? "Ctrl+C copies the selected text."
                    : "Drag across text to select it.";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool cropping = canvas.Tool == CanvasTool.Crop && video == null;
            switch (keyData)
            {
                case Keys.Control | Keys.N: StartSnip(); return true;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.C: Copy(); return true;
                case Keys.Control | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.Z: if (!cropping) canvas.Undo(); return true;
                case Keys.Control | Keys.Y: if (!cropping) canvas.Redo(); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: canvas.Zoom *= 1.25f; return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: canvas.Zoom /= 1.25f; return true;
                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0: FitZoom(); return true;
                case Keys.Enter:
                    if (!cropping) break;
                    ApplyCrop();
                    return true;
                case Keys.Escape:
                    if (!cropping && canvas.Tool != CanvasTool.Text) break;
                    canvas.Tool = previousTool;
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- Snips ---------------------------------------------------------

        private void StartSnip()
        {
            Hide();
            // Even with no delay, wait briefly so DWM has removed this window from the screen.
            captureTimer.Interval = Math.Max(300, settings.Delay * 1000);
            captureTimer.Start();
        }

        private void CaptureAndSelect()
        {
            Bitmap snip = null;
            Rectangle virtualBounds;
            using (Bitmap screen = ScreenCapture.CaptureVirtualScreen(settings.CaptureCursor, out virtualBounds))
            using (var overlay = new OverlayForm(screen, virtualBounds, settings.Mode, ScreenCapture.GetTopLevelWindows(), false))
            {
                if (overlay.ShowDialog() == DialogResult.OK) snip = overlay.Result;
                settings.Mode = overlay.Mode;
            }
            settings.Save();
            UpdateModeButton();

            Show();
            Activate();
            if (snip != null) LoadSnip(snip, true);
        }

        private void LoadSnip(Bitmap snip, bool fromCapture)
        {
            if (fromCapture && settings.AddBorder)
            {
                using (var g = Graphics.FromImage(snip))
                using (var pen = new Pen(Color.FromArgb(160, 160, 160)))
                {
                    g.DrawRectangle(pen, 0, 0, snip.Width - 1, snip.Height - 1);
                }
            }
            ClearVideo();
            canvas.SetImage(snip);
            FitWindowTo(canvas.ImageSize);
            FitZoom();

            if (!fromCapture) return;
            string message = null;
            if (settings.AutoCopy)
            {
                CopyImage();
                message = "Copied to clipboard";
            }
            if (settings.AutoSave)
            {
                string path = UniquePath(ScreenshotsFolder(), "Screenshot", ".png");
                using (Bitmap rendered = canvas.Render()) rendered.Save(path, ImageFormat.Png);
                message = (message == null ? "Saved" : message + " and saved") + " as " + Path.GetFileName(path);
            }
            if (message != null) toast.ShowMessage(message);
        }

        // ---- Recordings ----------------------------------------------------

        private void SetRecordMode(bool record)
        {
            settings.Record = record;
            settings.Save();
            UpdateModeButton();
            UpdateUi();
        }

        private void SelectRecordingArea()
        {
            Rectangle area = Rectangle.Empty;
            Rectangle virtualBounds;
            using (Bitmap screen = ScreenCapture.CaptureVirtualScreen(false, out virtualBounds))
            using (var overlay = new OverlayForm(screen, virtualBounds, settings.Mode, ScreenCapture.GetTopLevelWindows(), true))
            {
                if (overlay.ShowDialog() == DialogResult.OK) area = overlay.Selection;
                settings.Mode = overlay.Mode;
            }
            settings.Save();
            UpdateModeButton();

            if (area.Width < 32 || area.Height < 32)
            {
                Show();
                if (!area.IsEmpty) toast.ShowMessage("That area is too small to record");
                return;
            }

            string folder = settings.AutoSaveRecordings ? RecordingsFolder() : Path.Combine(Path.GetTempPath(), "SnipLab");
            var session = new RecordingSession(area, UniquePath(folder, "Screen Recording", ".mp4"), settings);
            session.Completed += OnRecordingCompleted;
            session.Show();
        }

        private void OnRecordingCompleted(RecordingResult result, string error)
        {
            Show();
            Activate();
            if (error != null)
            {
                MessageBox.Show(this, error, "SnipLab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (result == null) return;

            ClearVideo();
            video = result;
            videoView.SetVideo(result.Thumbnail, FormatDuration(result.Duration) + "  ·  " + Path.GetFileName(result.Path));
            FitWindowTo(result.Thumbnail != null ? result.Thumbnail.Size : new Size(Theme.Px(640), Theme.Px(360)));
            UpdateUi();

            string message = settings.AutoSaveRecordings ? "Saved to Screen Recordings" : "Recording ready";
            if (settings.AutoCopyRecordings)
            {
                CopyVideo();
                message += " and copied";
            }
            if (result.Warning != null) message += ". " + result.Warning;
            toast.ShowMessage(message);
        }

        private void ClearVideo()
        {
            if (video == null) return;
            if (video.Thumbnail != null) video.Thumbnail.Dispose();
            video = null;
            videoView.SetVideo(null, null);
            UpdateUi();
        }

        private void PlayVideo()
        {
            if (video != null) Process.Start(video.Path);
        }

        private void CopyVideo()
        {
            Clipboard.SetFileDropList(new StringCollection { video.Path });
        }

        private static string FormatDuration(TimeSpan t)
        {
            return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        // ---- Menus and flyouts -------------------------------------------

        private void ShowModeMenu()
        {
            ContextMenuStrip menu = Theme.CreateMenu();
            foreach (SnipMode mode in OverlayForm.Modes)
            {
                if (settings.Record && mode == SnipMode.Freeform) continue;
                var item = new ToolStripMenuItem(OverlayForm.ModeName(mode), Theme.GlyphImage(Glyphs.ForMode(mode), Theme.Text))
                {
                    Checked = settings.Mode == mode
                };
                item.Click += delegate
                {
                    settings.Mode = mode;
                    settings.Save();
                    UpdateModeButton();
                };
                menu.Items.Add(item);
            }
            menu.Show(modeButton, new Point(0, modeButton.Height));
        }

        private void ShowDelayMenu()
        {
            ContextMenuStrip menu = Theme.CreateMenu();
            foreach (int delay in Delays)
            {
                var item = new ToolStripMenuItem(delay == 0 ? "No delay" : "Start in " + delay + " seconds")
                {
                    Checked = settings.Delay == delay
                };
                item.Click += delegate
                {
                    settings.Delay = delay;
                    settings.Save();
                    UpdateDelayButton();
                };
                menu.Items.Add(item);
            }
            menu.Show(delayButton, new Point(0, delayButton.Height));
        }

        private void ShowMoreMenu(Control anchor)
        {
            ContextMenuStrip menu = Theme.CreateMenu();
            var open = new ToolStripMenuItem("Open file", Theme.GlyphImage(Glyphs.OpenFile, Theme.Text)) { ShortcutKeyDisplayString = "Ctrl+O" };
            open.Click += delegate { OpenFile(); };
            menu.Items.Add(open);
            menu.Items.Add(FolderItem("Open Screenshots folder", ScreenshotsFolder()));
            menu.Items.Add(FolderItem("Open Screen Recordings folder", RecordingsFolder()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(SettingItem("Automatically copy snips to clipboard", () => settings.AutoCopy, v => settings.AutoCopy = v));
            menu.Items.Add(SettingItem("Automatically save screenshots", () => settings.AutoSave, v => settings.AutoSave = v));
            menu.Items.Add(SettingItem("Capture mouse pointer", () => settings.CaptureCursor, v => settings.CaptureCursor = v));
            menu.Items.Add(SettingItem("Add border to each screenshot", () => settings.AddBorder, v => settings.AddBorder = v));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(SettingItem("Automatically save screen recordings", () => settings.AutoSaveRecordings, v => settings.AutoSaveRecordings = v));
            menu.Items.Add(SettingItem("Automatically copy screen recordings", () => settings.AutoCopyRecordings, v => settings.AutoCopyRecordings = v));
            menu.Show(anchor, new Point(anchor.Width - menu.PreferredSize.Width, anchor.Height));
        }

        private static ToolStripMenuItem FolderItem(string text, string folder)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += delegate
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            };
            return item;
        }

        private ToolStripMenuItem SettingItem(string text, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
            item.CheckedChanged += delegate
            {
                set(item.Checked);
                settings.Save();
            };
            return item;
        }

        // First click selects the tool; clicking the selected tool opens its options.
        private void ToolClicked(CanvasTool tool, IconButton button, Func<InkOptionsPanel> createOptions)
        {
            if (canvas.Tool != tool)
            {
                canvas.Tool = tool;
                return;
            }
            InkOptionsPanel options = createOptions();
            options.Changed += delegate { UpdateUi(); };
            var flyout = new Flyout(options);
            flyout.Closed += delegate { flyout.Dispose(); };
            flyout.ShowBelow(button);
        }

        private void EraserClicked()
        {
            if (canvas.Tool != CanvasTool.Eraser)
            {
                canvas.Tool = CanvasTool.Eraser;
                return;
            }
            ContextMenuStrip menu = Theme.CreateMenu();
            var eraseAll = new ToolStripMenuItem("Erase all ink", Theme.GlyphImage(Glyphs.Eraser, Theme.Text));
            eraseAll.Click += delegate { canvas.EraseAllInk(); };
            menu.Items.Add(eraseAll);
            menu.Show(eraserButton, new Point(0, eraserButton.Height));
        }

        private void UpdateModeButton()
        {
            SnipMode shown = settings.Record && settings.Mode == SnipMode.Freeform ? SnipMode.Rectangle : settings.Mode;
            modeButton.Glyph = Glyphs.ForMode(shown);
            Theme.ToolTips.SetToolTip(modeButton, (settings.Record ? "Recording area: " : "Snipping mode: ") + OverlayForm.ModeName(shown));
        }

        private void UpdateDelayButton()
        {
            delayButton.Label = settings.Delay == 0 ? null : settings.Delay + "s";
        }

        // ---- Crop and text actions ----------------------------------------

        private void EnterCrop()
        {
            if (canvas.Tool != CanvasTool.Text) previousTool = canvas.Tool;
            canvas.Tool = CanvasTool.Crop;
        }

        private void ApplyCrop()
        {
            canvas.ApplyCrop();
            canvas.Tool = previousTool;
            FitZoom();
        }

        private async void ToggleTextActions()
        {
            if (canvas.Tool == CanvasTool.Text)
            {
                canvas.Tool = previousTool;
                return;
            }
            previousTool = canvas.Tool;

            if (!canvas.HasTextResult)
            {
                Bitmap image = canvas.BaseImage;
                textButton.Enabled = false;
                toast.ShowMessage("Looking for text...");
                try
                {
                    var lines = await TextRecognizer.RecognizeAsync(image);
                    if (canvas.BaseImage != image) return; // a new snip arrived meanwhile
                    canvas.SetText(lines);
                }
                catch (Exception ex)
                {
                    toast.ShowMessage("Text actions are unavailable: " + ex.GetBaseException().Message);
                    return;
                }
                finally
                {
                    textButton.Enabled = true;
                }
            }
            canvas.Tool = CanvasTool.Text;
        }

        private void CopyAllText()
        {
            string text = canvas.GetAllText();
            if (text.Length == 0)
            {
                toast.ShowMessage("No text found");
                return;
            }
            Clipboard.SetText(text);
            toast.ShowMessage("All text copied");
        }

        private void QuickRedact()
        {
            int count = canvas.RedactSensitiveText();
            toast.ShowMessage(count == 0 ? "No email addresses or phone numbers found" : "Redacted " + count + " item" + (count == 1 ? "" : "s"));
        }

        // ---- Layout --------------------------------------------------------

        private void FitWindowTo(Size content)
        {
            if (WindowState != FormWindowState.Normal) return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int pad = Theme.Px(24);
            Size chrome = Size - ClientSize;
            int width = Math.Max(Theme.Px(760), content.Width + pad * 2) + chrome.Width;
            int height = Math.Max(Theme.Px(420), content.Height + pad * 2 + toolbar.Height + textBar.Height) + chrome.Height;
            width = Math.Min(width, area.Width * 85 / 100);
            height = Math.Min(height, area.Height * 85 / 100);
            int left = Math.Max(area.Left, Math.Min(Left, area.Right - width));
            int top = Math.Max(area.Top, Math.Min(Top, area.Bottom - height));
            Bounds = new Rectangle(left, top, width, height);
        }

        private void FitZoom()
        {
            if (!canvas.HasImage) return;
            int pad = Theme.Px(24);
            Size image = canvas.ImageSize;
            float zoom = Math.Min(1f, Math.Min(
                (scroller.ClientSize.Width - pad * 2) / (float)image.Width,
                (scroller.ClientSize.Height - pad * 2) / (float)image.Height));
            canvas.Zoom = Math.Max(0.1f, zoom);
            zoomLabel.Text = (int)Math.Round(canvas.Zoom * 100) + "%";
            CenterCanvas();
        }

        // Centres the canvas when it is smaller than the view; otherwise lets it scroll.
        private void CenterCanvas()
        {
            int pad = Theme.Px(24);
            scroller.AutoScrollMinSize = new Size(canvas.Width + pad * 2, canvas.Height + pad * 2);
            Size view = scroller.ClientSize;
            int x = canvas.Width + pad * 2 < view.Width ? (view.Width - canvas.Width) / 2 : scroller.AutoScrollPosition.X + pad;
            int y = canvas.Height + pad * 2 < view.Height ? (view.Height - canvas.Height) / 2 : scroller.AutoScrollPosition.Y + pad;
            canvas.Location = new Point(x, y);
        }

        // ---- Clipboard and files -----------------------------------------

        private void Copy()
        {
            if (video != null)
            {
                CopyVideo();
                toast.ShowMessage("Recording copied");
                return;
            }
            if (!canvas.HasImage) return;
            if (canvas.Tool == CanvasTool.Text && canvas.HasTextSelection)
            {
                Clipboard.SetText(canvas.GetSelectedText());
                toast.ShowMessage("Selected text copied");
                return;
            }
            CopyImage();
            toast.ShowMessage("Copied to clipboard");
        }

        // Puts a PNG (keeps freeform transparency) and a plain bitmap (for apps that
        // only read CF_BITMAP) on the clipboard.
        private void CopyImage()
        {
            using (Bitmap rendered = canvas.Render())
            using (Bitmap flat = FlattenOnWhite(rendered))
            using (var png = new MemoryStream())
            {
                rendered.Save(png, ImageFormat.Png);
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, flat);
                data.SetData("PNG", false, png);
                Clipboard.SetDataObject(data, true, 5, 100);
            }
        }

        private void OpenFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";
                dialog.InitialDirectory = ScreenshotsFolder();
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                Bitmap copy;
                using (Image file = Image.FromFile(dialog.FileName))
                {
                    copy = new Bitmap(file.Width, file.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImage(file, new Rectangle(0, 0, file.Width, file.Height));
                }
                LoadSnip(copy, false);
            }
        }

        private void SaveAs()
        {
            if (video != null)
            {
                SaveVideoAs();
                return;
            }
            if (!canvas.HasImage) return;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG image|*.png|JPEG image|*.jpg";
                dialog.InitialDirectory = ScreenshotsFolder();
                dialog.FileName = "Screenshot " + Timestamp() + ".png";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                using (Bitmap rendered = canvas.Render())
                {
                    if (dialog.FilterIndex == 2)
                    {
                        using (Bitmap flat = FlattenOnWhite(rendered)) flat.Save(dialog.FileName, ImageFormat.Jpeg);
                    }
                    else
                    {
                        rendered.Save(dialog.FileName, ImageFormat.Png);
                    }
                }
                toast.ShowMessage("Saved as " + Path.GetFileName(dialog.FileName));
            }
        }

        private void SaveVideoAs()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "MP4 video|*.mp4";
                dialog.InitialDirectory = RecordingsFolder();
                dialog.FileName = Path.GetFileName(video.Path);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(video.Path), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(video.Path, dialog.FileName, true);
                }
                toast.ShowMessage("Saved as " + Path.GetFileName(dialog.FileName));
            }
        }

        // "<prefix> <date time>.ext", with " (2)", " (3)"... added if that name is taken.
        private static string UniquePath(string folder, string prefix, string extension)
        {
            Directory.CreateDirectory(folder);
            string name = prefix + " " + Timestamp();
            string path = Path.Combine(folder, name + extension);
            for (int n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(folder, name + " (" + n + ")" + extension);
            }
            return path;
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        }

        private static string ScreenshotsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }

        private static string RecordingsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");
        }

        private static Bitmap FlattenOnWhite(Bitmap source)
        {
            var flat = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
            }
            return flat;
        }
    }
}
