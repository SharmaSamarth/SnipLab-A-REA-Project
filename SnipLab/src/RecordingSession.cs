using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SnipLab
{
    internal sealed class RecordingResult
    {
        public string Path;
        public Bitmap Thumbnail;
        public TimeSpan Duration;
        public string Warning;
    }

    // The floating recording bar: Start (with a 3-2-1 countdown), microphone and
    // system-audio toggles, then Pause/Resume, Stop, a timer and Delete. It owns the
    // outline around the recorded area and the ScreenRecorder. Both windows are
    // excluded from captures, so they never appear in the video.
    internal sealed class RecordingSession : Form
    {
        private const int CountdownSeconds = 3;

        private readonly Rectangle region;
        private readonly string outputPath;
        private readonly Settings settings;
        private readonly RecordingFrame frame;
        private readonly ToolbarPanel bar = new ToolbarPanel();
        private readonly Timer timer = new Timer { Interval = 200 };
        private readonly Label timeLabel = new Label();
        private readonly IconButton startButton = new IconButton(Glyphs.Record, "Start recording") { Label = "Start" };
        private readonly IconButton micButton = new IconButton(Glyphs.Microphone, "Microphone");
        private readonly IconButton audioButton = new IconButton(Glyphs.Volume, "System audio");
        private readonly IconButton closeButton = new IconButton(Glyphs.Close, "Close");
        private readonly IconButton pauseButton = new IconButton(Glyphs.Pause, "Pause");
        private readonly IconButton stopButton = new IconButton(Glyphs.Stop, "Stop recording");
        private readonly IconButton deleteButton = new IconButton(Glyphs.Delete, "Delete recording");
        private ScreenRecorder recorder;
        private DateTime countdownEnd;
        private bool finished;

        // Raised once: (result, null) when saved, (null, null) when cancelled, (null, error) when it failed.
        public event Action<RecordingResult, string> Completed;

        public RecordingSession(Rectangle region, string outputPath, Settings settings)
        {
            this.region = region;
            this.outputPath = outputPath;
            this.settings = settings;
            frame = new RecordingFrame(region);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Theme.Surface;
            Font = Theme.UiFont;
            Text = "SnipLab recording";

            timeLabel.AutoSize = false;
            timeLabel.Size = new Size(Theme.Px(72), Theme.Px(36));
            timeLabel.TextAlign = ContentAlignment.MiddleCenter;
            timeLabel.ForeColor = Theme.Text;
            timeLabel.Margin = new Padding(Theme.Px(1));

            micButton.Checked = settings.RecordMicrophone;
            audioButton.Checked = settings.RecordSystemAudio;
            UpdateAudioToolTips();

            startButton.Click += delegate { BeginCountdown(); };
            micButton.Click += delegate
            {
                settings.RecordMicrophone = micButton.Checked = !micButton.Checked;
                settings.Save();
                UpdateAudioToolTips();
            };
            audioButton.Click += delegate
            {
                settings.RecordSystemAudio = audioButton.Checked = !audioButton.Checked;
                settings.Save();
                UpdateAudioToolTips();
            };
            closeButton.Click += delegate { Finish(null, null); };
            pauseButton.Click += delegate { TogglePause(); };
            stopButton.Click += delegate { StopRecording(); };
            deleteButton.Click += delegate { DeleteRecording(); };
            timer.Tick += delegate { OnTick(); };

            bar.BackColor = Theme.Surface;
            bar.Padding = new Padding(Theme.Px(4));
            bar.MouseDown += (sender, e) => DragWindow(e);
            timeLabel.MouseDown += (sender, e) => DragWindow(e);
            bar.Add(startButton, micButton, audioButton, pauseButton, stopButton, timeLabel, new ToolbarSeparator(), closeButton, deleteButton);
            Controls.Add(bar);
            ShowReadyState();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            frame.Show();
            PlaceBar();
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        // Centred above the area, or just inside its top edge when there is no room above.
        private void PlaceBar()
        {
            Size = bar.PreferredSize;
            Rectangle area = Screen.FromRectangle(region).WorkingArea;
            int gap = Theme.Px(10);
            int x = region.X + (region.Width - Width) / 2;
            int y = region.Y - Height - gap;
            if (y < area.Top) y = region.Y + gap;
            x = Math.Max(area.Left, Math.Min(x, area.Right - Width));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - Height));
            Location = new Point(x, y);
        }

        private void ShowReadyState()
        {
            startButton.Visible = micButton.Visible = audioButton.Visible = closeButton.Visible = true;
            pauseButton.Visible = stopButton.Visible = deleteButton.Visible = timeLabel.Visible = false;
        }

        private void ShowRecordingState()
        {
            startButton.Visible = micButton.Visible = audioButton.Visible = closeButton.Visible = false;
            pauseButton.Visible = stopButton.Visible = deleteButton.Visible = timeLabel.Visible = true;
            PlaceBar();
        }

        private void UpdateAudioToolTips()
        {
            Theme.ToolTips.SetToolTip(micButton, "Microphone: " + (micButton.Checked ? "on" : "off"));
            Theme.ToolTips.SetToolTip(audioButton, "System audio: " + (audioButton.Checked ? "on" : "off"));
        }

        private void BeginCountdown()
        {
            ShowRecordingState();
            pauseButton.Enabled = stopButton.Enabled = false;
            countdownEnd = DateTime.UtcNow.AddSeconds(CountdownSeconds);
            OnTick();
            timer.Start();
        }

        private void OnTick()
        {
            if (recorder == null)
            {
                double left = (countdownEnd - DateTime.UtcNow).TotalSeconds;
                if (left > 0)
                {
                    timeLabel.Text = "Starting in " + Math.Ceiling(left);
                    return;
                }
                StartRecording();
            }
            if (recorder.HasFailed)
            {
                StopRecording();
                return;
            }
            TimeSpan t = recorder.Elapsed;
            timeLabel.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private void StartRecording()
        {
            recorder = new ScreenRecorder(region, outputPath, settings.CaptureCursor, settings.RecordSystemAudio, settings.RecordMicrophone);
            recorder.Start();
            frame.Recording = true;
            pauseButton.Enabled = stopButton.Enabled = true;
        }

        private void TogglePause()
        {
            recorder.Paused = !recorder.Paused;
            pauseButton.Glyph = recorder.Paused ? Glyphs.Play : Glyphs.Pause;
            Theme.ToolTips.SetToolTip(pauseButton, recorder.Paused ? "Resume" : "Pause");
            frame.Recording = !recorder.Paused;
        }

        private async void StopRecording()
        {
            if (recorder == null || finished) return;
            timer.Stop();
            pauseButton.Enabled = stopButton.Enabled = deleteButton.Enabled = false;
            timeLabel.Text = "Saving...";
            TimeSpan duration = recorder.Elapsed;
            try
            {
                await recorder.StopAsync();
            }
            catch (Exception ex)
            {
                TryDelete();
                Finish(null, "The recording could not be saved: " + ex.GetBaseException().Message);
                return;
            }
            Finish(new RecordingResult
            {
                Path = outputPath,
                Thumbnail = recorder.FirstFrame,
                Duration = duration,
                Warning = recorder.AudioWarning
            }, null);
        }

        private async void DeleteRecording()
        {
            if (finished) return;
            timer.Stop();
            pauseButton.Enabled = stopButton.Enabled = deleteButton.Enabled = false;
            if (recorder != null)
            {
                try
                {
                    await recorder.StopAsync();
                }
                catch (Exception)
                {
                    // The file is deleted anyway.
                }
                TryDelete();
            }
            Finish(null, null);
        }

        private void TryDelete()
        {
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
            catch (IOException)
            {
            }
        }

        private void Finish(RecordingResult result, string error)
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            frame.Close();
            Close();
            if (Completed != null) Completed(result, error);
        }

        // Lets the user drag the bar by its background, like a title bar.
        private void DragWindow(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                frame.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Outline around the recorded area: click-through, never takes focus, and
    // excluded from captures. Blue while waiting, red while recording.
    internal sealed class RecordingFrame : Form
    {
        private bool recording;

        public RecordingFrame(Rectangle region)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            int thickness = Theme.Px(2);
            Bounds = Rectangle.Inflate(region, thickness, thickness);
        }

        public bool Recording
        {
            set
            {
                recording = value;
                Invalidate();
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int t = Theme.Px(2);
            using (var brush = new SolidBrush(recording ? Color.FromArgb(232, 17, 35) : Theme.Accent))
            {
                e.Graphics.FillRectangle(brush, 0, 0, Width, t);
                e.Graphics.FillRectangle(brush, 0, Height - t, Width, t);
                e.Graphics.FillRectangle(brush, 0, 0, t, Height);
                e.Graphics.FillRectangle(brush, Width - t, 0, t, Height);
            }
        }
    }

    // Result screen for a recording: the first frame with a play button. Clicking
    // opens the video in the default player.
    internal sealed class VideoView : Control
    {
        private Bitmap thumbnail;
        private string caption;
        private bool hover;

        public VideoView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CanvasBack;
            Cursor = Cursors.Hand;
            Theme.ToolTips.SetToolTip(this, "Play");
        }

        public void SetVideo(Bitmap image, string text)
        {
            thumbnail = image;
            caption = text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int pad = Theme.Px(24), captionHeight = Theme.Px(28);
            var area = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2 - captionHeight);
            if (area.Width <= 0 || area.Height <= 0) return;

            Rectangle picture = area;
            if (thumbnail != null)
            {
                float scale = Math.Min(1f, Math.Min(area.Width / (float)thumbnail.Width, area.Height / (float)thumbnail.Height));
                var size = new Size((int)(thumbnail.Width * scale), (int)(thumbnail.Height * scale));
                picture = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(thumbnail, picture);
            }
            else
            {
                g.FillRectangle(Brushes.Black, picture);
            }
            if (hover)
            {
                using (var shade = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillRectangle(shade, picture);
            }

            int d = Theme.Px(64);
            var circle = new Rectangle(picture.X + (picture.Width - d) / 2, picture.Y + (picture.Height - d) / 2, d, d);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillEllipse(back, circle);
            Theme.DrawGlyph(g, Glyphs.Play, circle, Color.White, 20f);

            var captionBounds = new Rectangle(0, picture.Bottom + Theme.Px(6), Width, captionHeight);
            TextRenderer.DrawText(g, caption, Theme.UiFont, captionBounds, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
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
}
