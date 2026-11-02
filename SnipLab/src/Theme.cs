using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnipLab
{
    // Windows 11-style colours, icon font and sizing. Follows the system's
    // light/dark app setting and accent colour.
    internal static class Theme
    {
        public static readonly bool IsDark = ReadIsDark();
        public static readonly Color Background = IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        public static readonly Color Surface = IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(252, 252, 252);
        public static readonly Color CanvasBack = IsDark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(228, 228, 228);
        public static readonly Color Hover = IsDark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(229, 229, 229);
        public static readonly Color Pressed = IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(218, 218, 218);
        public static readonly Color Selected = IsDark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(222, 222, 222);
        public static readonly Color Border = IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(214, 214, 214);
        public static readonly Color Text = IsDark ? Color.White : Color.FromArgb(26, 26, 26);
        public static readonly Color TextMuted = IsDark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(100, 100, 100);
        public static readonly Color Disabled = IsDark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(165, 165, 165);
        public static readonly Color Accent = ReadAccent();

        public static readonly float Dpi = ReadDpi();
        public static readonly Font UiFont = new Font("Segoe UI", 9f);
        public static readonly ToolTip ToolTips = new ToolTip();

        private static readonly string IconFontName = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        private static readonly Dictionary<float, Font> IconFonts = new Dictionary<float, Font>();

        // Scales a 96-DPI pixel size to the current screen.
        public static int Px(int value)
        {
            return (int)Math.Round(value * Dpi / 96f);
        }

        public static Font IconFont(float points)
        {
            Font font;
            if (!IconFonts.TryGetValue(points, out font))
            {
                font = new Font(IconFontName, points);
                IconFonts[points] = font;
            }
            return font;
        }

        public static void DrawGlyph(Graphics g, string glyph, Rectangle bounds, Color color, float points)
        {
            TextRenderer.DrawText(g, glyph, IconFont(points), bounds, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // Menu items need a Bitmap; GDI+ text keeps the transparent background clean.
        public static Bitmap GlyphImage(string glyph, Color color)
        {
            int size = Px(16);
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph, IconFont(10f), brush, new RectangleF(0, 0, size, size), format);
            }
            return bitmap;
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundedRect(r, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        // Dark title bar to match the window contents (DWMWA_USE_IMMERSIVE_DARK_MODE).
        public static void ApplyTitleBar(Form form)
        {
            int dark = IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        public static ContextMenuStrip CreateMenu()
        {
            return new ContextMenuStrip
            {
                Renderer = new MenuRenderer(),
                Font = UiFont,
                ShowImageMargin = true,
                ShowCheckMargin = true
            };
        }

        private static bool ReadIsDark()
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return value is int && (int)value == 0;
        }

        // DWM stores the accent as 0xAABBGGRR.
        private static Color ReadAccent()
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (!(value is int)) return Color.FromArgb(0, 120, 212);
            int abgr = (int)value;
            Color accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            return IsDark ? ControlPaint.Light(accent, 0.6f) : accent;
        }

        private static float ReadDpi()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return g.DpiX;
            }
        }

        private static bool HasFont(string name)
        {
            using (var fonts = new InstalledFontCollection())
            {
                return fonts.Families.Any(f => f.Name == name);
            }
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Pressed; } }
            public override Color MenuItemPressedGradientEnd { get { return Pressed; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Surface; } }
            public override Color CheckBackground { get { return Surface; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Pressed; } }
        }

        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new MenuColors())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Disabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                DrawGlyph(e.Graphics, Glyphs.Check, e.ImageRectangle, Text, 9f);
            }
        }
    }

    // Segoe Fluent Icons code points (checked against the font on this machine).
    internal static class Glyphs
    {
        public const string Add = "";
        public const string Close = "";
        public const string More = "";
        public const string Settings = "";
        public const string Save = "";
        public const string Copy = "";
        public const string Undo = "";
        public const string Redo = "";
        public const string ZoomIn = "";
        public const string ZoomOut = "";
        public const string Crop = "";
        public const string Pen = "";
        public const string Highlighter = "";
        public const string Eraser = "";
        public const string Clock = "";
        public const string OpenFile = "";
        public const string ChevronDown = "";
        public const string Check = "";
        public const string TextActions = "";
        public const string RectangleMode = "";
        public const string FreeformMode = "";
        public const string WindowMode = "";
        public const string FullScreenMode = "";
        public const string Snip = "";
        public const string Camera = "";
        public const string Video = "";
        public const string Record = "";
        public const string Play = "";
        public const string Pause = "";
        public const string Stop = "";
        public const string Microphone = "";
        public const string Volume = "";
        public const string Delete = "";

        public static string ForMode(SnipMode mode)
        {
            switch (mode)
            {
                case SnipMode.Window: return WindowMode;
                case SnipMode.FullScreen: return FullScreenMode;
                case SnipMode.Freeform: return FreeformMode;
                default: return RectangleMode;
            }
        }
    }
}
