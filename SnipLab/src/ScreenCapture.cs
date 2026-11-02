using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipLab
{
    internal static class ScreenCapture
    {
        // Grabs every monitor in one bitmap so the overlay can show a frozen desktop.
        public static Bitmap CaptureVirtualScreen(bool includeCursor, out Rectangle bounds)
        {
            bounds = SystemInformation.VirtualScreen;
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                if (includeCursor)
                {
                    DrawCursor(g, bounds.Location);
                }
            }
            return bitmap;
        }

        // Screen copies never include the mouse pointer, so it is drawn in afterwards.
        public static void DrawCursor(Graphics g, Point origin)
        {
            var info = new NativeMethods.CURSORINFO();
            info.cbSize = Marshal.SizeOf(info);
            if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0)
            {
                return;
            }

            int hotspotX = 0, hotspotY = 0;
            NativeMethods.ICONINFO icon;
            if (NativeMethods.GetIconInfo(info.hCursor, out icon))
            {
                hotspotX = icon.xHotspot;
                hotspotY = icon.yHotspot;
                if (icon.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmMask);
                if (icon.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmColor);
            }

            IntPtr hdc = g.GetHdc();
            try
            {
                NativeMethods.DrawIcon(hdc,
                    info.ptScreenPos.X - origin.X - hotspotX,
                    info.ptScreenPos.Y - origin.Y - hotspotY,
                    info.hCursor);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        // Visible top-level windows in z-order (front first). Bounds come from DWM's
        // extended frame so the invisible resize border is not part of the snip.
        public static List<Rectangle> GetTopLevelWindows()
        {
            var result = new List<Rectangle>();
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;
                if ((NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
                if (NativeMethods.GetWindowTextLength(hWnd) == 0) return true;

                // Cloaked windows are "visible" but hidden by DWM (e.g. apps on other virtual desktops).
                int cloaked;
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                NativeMethods.RECT rect;
                int rectSize = Marshal.SizeOf(typeof(NativeMethods.RECT));
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, rectSize) != 0
                    && !NativeMethods.GetWindowRect(hWnd, out rect))
                {
                    return true;
                }

                Rectangle bounds = rect.ToRectangle();
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    result.Add(bounds);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
