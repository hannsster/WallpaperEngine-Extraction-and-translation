using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Applies the documented Windows 11 DWM backdrop and dark title bar. On older
/// builds the calls fail harmlessly and the app's own dark glass gradient remains.
/// No undocumented window-composition APIs are used.
/// </summary>
public static class AcrylicHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmsbtAuto = 0;
    private const int DwmsbtMainWindow = 2;   // Mica
    private const int DwmsbtTransientWindow = 3; // Acrylic

    public static bool AppliedBackdrop { get; private set; }

    public static void Enable(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            }

            var dark = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
            }

            var rounded = 2;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));

            var acrylic = DwmsbtTransientWindow;
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref acrylic, sizeof(int));
            if (result != 0)
            {
                var mica = DwmsbtMainWindow;
                result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int));
            }

            AppliedBackdrop = result == 0;
            if (!AppliedBackdrop)
            {
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref UnsafeZero, sizeof(int));
            }

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            Logger.Info($"DWM backdrop applied={AppliedBackdrop}; Mica/Acrylic is a Windows 11 22H2+ capability.");
        }
        catch (Exception ex)
        {
            AppliedBackdrop = false;
            Logger.Warn("Could not enable DWM backdrop; using in-app glass gradient instead.", ex);
        }
    }

    private static int UnsafeZero;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }
}
