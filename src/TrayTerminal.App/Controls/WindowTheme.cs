using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace TrayTerminal.App.Controls;

/// <summary>
/// Paints the native title bar and window border in the app's dark chrome
/// colors, so they blend with the toolbar instead of staying light.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            // Windows 10 understands only dark mode; the colors need Windows 11.
            // Unsupported attributes just return an error and change nothing.
            SetAttribute(handle, DwmwaUseImmersiveDarkMode, 1);
            SetColor(handle, DwmwaCaptionColor, "WindowBackgroundBrush");
            SetColor(handle, DwmwaBorderColor, "BorderBrush");
            SetColor(handle, DwmwaTextColor, "MutedTextBrush");
        };
    }

    private static void SetColor(IntPtr handle, int attribute, string brushKey)
    {
        if (System.Windows.Application.Current?.TryFindResource(brushKey) is SolidColorBrush brush)
        {
            SetAttribute(handle, attribute, ToColorRef(brush.Color));
        }
    }

    private static int ToColorRef(WpfColor color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value)
    {
        if (handle != IntPtr.Zero)
        {
            _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
