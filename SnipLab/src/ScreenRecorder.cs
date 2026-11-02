using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnipLab
{
    // Records a screen region to MP4 (H.264 video, AAC audio). A capture thread
    // copies the region 30 times a second; Windows' MediaTranscoder pulls those
    // frames, plus mixed audio, through a MediaStreamSource and encodes them.
    // Snipping Tool does the same job with Media Foundation directly (MFPLAT.DLL).
    internal sealed class ScreenRecorder
    {
        private const int FramesPerSecond = 30;
        private const int AudioChunkFrames = AudioCapture.SampleRate / 50; // 20 ms
        private const int MaxAudioWaitMs = 250;
        private static readonly TimeSpan AudioLatency = TimeSpan.FromMilliseconds(60);

        private sealed class Frame
        {
            public byte[] Pixels;
            public TimeSpan Time;
        }

        private readonly Rectangle region;
        private readonly string outputPath;
        private readonly bool includeCursor;
        private readonly List<AudioCapture> audioSources = new List<AudioCapture>();
        private readonly BlockingCollection<Frame> frames = new BlockingCollection<Frame>(8);
        // Frame buffers are reused: allocating a new multi-megabyte array 30 times a
        // second made the garbage collector pause the whole app about once a second,
        // which starved the audio thread and broke up microphone audio.
        private readonly ConcurrentBag<byte[]> pixelPool = new ConcurrentBag<byte[]>();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly byte[] audioMix = new byte[AudioChunkFrames * AudioCapture.BytesPerFrame];
        private readonly byte[] audioScratch = new byte[AudioChunkFrames * AudioCapture.BytesPerFrame];
        private Thread captureThread;
        private Task encodeTask;
        private long audioFramesWritten;
        private TimeSpan stopTime;
        private volatile bool stopping;
        private volatile bool paused;

        public ScreenRecorder(Rectangle region, string outputPath, bool includeCursor, bool systemAudio, bool microphone)
        {
            // H.264 needs even dimensions.
            this.region = new Rectangle(region.X, region.Y, region.Width & ~1, region.Height & ~1);
            this.outputPath = outputPath;
            this.includeCursor = includeCursor;
            if (systemAudio) TryAddAudio(true, "system audio");
            if (microphone) TryAddAudio(false, "the microphone");
        }

        public string AudioWarning { get; private set; }

        public Bitmap FirstFrame { get; private set; }

        public TimeSpan Elapsed
        {
            get { return clock.Elapsed; }
        }

        public bool HasFailed
        {
            get { return encodeTask != null && encodeTask.IsFaulted; }
        }

        public bool Paused
        {
            get { return paused; }
            set
            {
                paused = value;
                if (value) clock.Stop();
                else clock.Start();
                foreach (AudioCapture source in audioSources) source.Paused = value;
            }
        }

        public void Start()
        {
            clock.Start();
            captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Screen capture" };
            captureThread.Start();
            encodeTask = Task.Run(() => Encode());
        }

        // Finishes the file. Throws if encoding failed.
        public async Task StopAsync()
        {
            stopTime = clock.Elapsed;
            stopping = true;
            try
            {
                await encodeTask;
            }
            finally
            {
                foreach (AudioCapture source in audioSources) source.Dispose();
            }
        }

        private void TryAddAudio(bool systemAudio, string name)
        {
            try
            {
                audioSources.Add(new AudioCapture(systemAudio));
            }
            catch (Exception ex)
            {
                AudioWarning = "Could not record " + name + " (" + ex.Message + ")";
            }
        }

        private void CaptureLoop()
        {
            using (var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                long nextFrame = 0;
                while (!stopping)
                {
                    TimeSpan now = clock.Elapsed;
                    TimeSpan due = TimeSpan.FromTicks(nextFrame * TimeSpan.TicksPerSecond / FramesPerSecond);
                    if (paused || now < due)
                    {
                        Thread.Sleep(paused ? 20 : Math.Max(1, (int)(due - now).TotalMilliseconds));
                        continue;
                    }

                    try
                    {
                        g.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
                    }
                    catch (Win32Exception)
                    {
                        // Secure desktop (UAC prompt, lock screen): keep the previous frame.
                    }
                    if (includeCursor) ScreenCapture.DrawCursor(g, region.Location);
                    if (FirstFrame == null) FirstFrame = new Bitmap(bitmap);

                    byte[] pixels;
                    if (!pixelPool.TryTake(out pixels)) pixels = new byte[region.Width * 4 * region.Height];
                    ReadPixels(bitmap, pixels);

                    // If the encoder falls behind, drop the frame rather than block capture.
                    if (!frames.TryAdd(new Frame { Pixels = pixels, Time = now })) pixelPool.Add(pixels);

                    // Skip slots that have already passed so a slow capture does not fall behind.
                    nextFrame = now.Ticks * FramesPerSecond / TimeSpan.TicksPerSecond + 1;
                }
            }
            frames.CompleteAdding();
        }

        // Media Foundation reads uncompressed RGB bottom row first, while GDI+ bitmaps
        // are stored top row first, so the rows are copied in reverse order.
        private static void ReadPixels(Bitmap bitmap, byte[] pixels)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                for (int y = 0; y < data.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + (data.Height - 1 - y) * stride, pixels, y * stride, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private void Encode()
        {
            var video = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)region.Width, (uint)region.Height));
            MediaStreamSource source;
            if (audioSources.Count > 0)
            {
                var audio = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioCapture.SampleRate, AudioCapture.Channels, 16));
                source = new MediaStreamSource(video, audio);
            }
            else
            {
                source = new MediaStreamSource(video);
            }
            source.BufferTime = TimeSpan.Zero;
            source.SampleRequested += (sender, args) =>
            {
                MediaStreamSourceSampleRequest request = args.Request;
                request.Sample = request.StreamDescriptor is VideoStreamDescriptor ? NextVideoSample() : NextAudioSample();
            };

            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)region.Width;
            profile.Video.Height = (uint)region.Height;
            profile.Video.FrameRate.Numerator = FramesPerSecond;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            // About 8 Mbit/s for 1080p; screen content needs more than camera video.
            profile.Video.Bitrate = (uint)Math.Max(2000000, Math.Min(20000000, region.Width * region.Height * 4));
            profile.Audio = audioSources.Count > 0 ? AudioEncodingProperties.CreateAac(AudioCapture.SampleRate, AudioCapture.Channels, 192000) : null;

            StorageFolder folder = WinRtAsync.Wait(StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)));
            StorageFile file = WinRtAsync.Wait(folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting));
            IRandomAccessStream stream = WinRtAsync.Wait(file.OpenAsync(FileAccessMode.ReadWrite));
            try
            {
                var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                PrepareTranscodeResult prepared = WinRtAsync.Wait(transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile));
                if (!prepared.CanTranscode)
                {
                    throw new InvalidOperationException("Windows cannot encode this recording (" + prepared.FailureReason + ").");
                }
                WinRtAsync.Wait(prepared.TranscodeAsync());
            }
            finally
            {
                ((IDisposable)stream).Dispose();
            }
        }

        // Blocks until the capture thread has a frame; null ends the video stream.
        private MediaStreamSample NextVideoSample()
        {
            Frame frame;
            if (!frames.TryTake(out frame, Timeout.Infinite)) return null;
            // CreateFromByteArray copies the pixels, so the array can be reused straight away.
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(frame.Pixels), frame.Time);
            pixelPool.Add(frame.Pixels);
            return sample;
        }

        // Hands out 20 ms of mixed audio, paced by the recording clock so every
        // source has captured that stretch before it is read.
        private MediaStreamSample NextAudioSample()
        {
            TimeSpan time = TimeSpan.FromTicks(audioFramesWritten * TimeSpan.TicksPerSecond / AudioCapture.SampleRate);
            TimeSpan duration = TimeSpan.FromTicks(AudioChunkFrames * TimeSpan.TicksPerSecond / AudioCapture.SampleRate);
            while (!stopping && clock.Elapsed < time + duration + AudioLatency)
            {
                Thread.Sleep(5);
            }
            if (stopping && time >= stopTime) return null;

            for (int s = 0; s < audioSources.Count; s++)
            {
                Array.Clear(audioScratch, 0, audioScratch.Length);
                ReadChunk(audioSources[s]); // anything not filled stays silent
                if (s == 0)
                {
                    System.Buffer.BlockCopy(audioScratch, 0, audioMix, 0, audioMix.Length);
                    continue;
                }
                for (int i = 0; i < audioMix.Length; i += 2)
                {
                    int sum = (short)(audioMix[i] | audioMix[i + 1] << 8) + (short)(audioScratch[i] | audioScratch[i + 1] << 8);
                    sum = Math.Max(short.MinValue, Math.Min(short.MaxValue, sum));
                    audioMix[i] = (byte)sum;
                    audioMix[i + 1] = (byte)(sum >> 8);
                }
            }
            audioFramesWritten += AudioChunkFrames;

            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(audioMix), time);
            sample.Duration = duration;
            return sample;
        }

        // A microphone delivers audio continuously, so a short chunk means the audio is
        // only late: wait for it instead of inserting a gap (which was heard as the voice
        // breaking up). Loopback sends nothing while the PC is silent, so an idle source
        // is padded with silence straight away.
        private void ReadChunk(AudioCapture source)
        {
            int copied = source.Read(audioScratch, 0, audioScratch.Length);
            var waited = Stopwatch.StartNew();
            while (copied < audioScratch.Length && source.IsActive && !stopping && waited.ElapsedMilliseconds < MaxAudioWaitMs)
            {
                Thread.Sleep(2);
                copied += source.Read(audioScratch, copied, audioScratch.Length - copied);
            }
        }
    }
}
