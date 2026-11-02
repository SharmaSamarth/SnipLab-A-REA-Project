using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace SnipLab
{
    internal sealed class RecognizedWord
    {
        public string Text;
        public RectangleF Bounds;
    }

    internal sealed class RecognizedLine
    {
        public readonly List<RecognizedWord> Words = new List<RecognizedWord>();

        public string Text
        {
            get { return string.Join(" ", Words.Select(w => w.Text)); }
        }
    }

    // Text actions, built on the OCR engine that ships with Windows
    // (Windows.Media.Ocr). Snipping Tool ships its own engine (oneocr.dll).
    internal static class TextRecognizer
    {
        public static bool IsAvailable
        {
            get { return OcrEngine.TryCreateFromUserProfileLanguages() != null; }
        }

        public static Task<List<RecognizedLine>> RecognizeAsync(Bitmap image)
        {
            // Copy the pixels on the calling thread; GDI+ bitmaps are not thread-safe.
            byte[] pixels;
            int width = image.Width, height = image.Height;
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                pixels = new byte[data.Stride * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                image.UnlockBits(data);
            }

            return Task.Run(() =>
            {
                OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null) throw new InvalidOperationException("No OCR language is installed for your Windows display language.");

                // Format32bppArgb is laid out B, G, R, A in memory, which is Bgra8.
                SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    CryptographicBuffer.CreateFromByteArray(pixels), BitmapPixelFormat.Bgra8, width, height);
                OcrResult result = WinRtAsync.Wait(engine.RecognizeAsync(bitmap));

                var lines = new List<RecognizedLine>();
                foreach (OcrLine line in result.Lines)
                {
                    var recognized = new RecognizedLine();
                    foreach (OcrWord word in line.Words)
                    {
                        Rect r = word.BoundingRect;
                        recognized.Words.Add(new RecognizedWord
                        {
                            Text = word.Text,
                            Bounds = new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height)
                        });
                    }
                    lines.Add(recognized);
                }
                return lines;
            });
        }

        public static string JoinLines(IEnumerable<RecognizedLine> lines)
        {
            var text = new StringBuilder();
            foreach (RecognizedLine line in lines)
            {
                if (text.Length > 0) text.AppendLine();
                text.Append(line.Text);
            }
            return text.ToString();
        }
    }
}
