using System;
using System.Windows.Forms;

namespace SnipLab
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // Without this, Windows scales the app on high-DPI screens and screen
        // coordinates no longer match real pixels, so snips come out offset or blurry.
        private static void EnableDpiAwareness()
        {
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
            }
            catch (EntryPointNotFoundException)
            {
                // Older than Windows 10 1703.
            }
            NativeMethods.SetProcessDPIAware();
        }
    }
}
