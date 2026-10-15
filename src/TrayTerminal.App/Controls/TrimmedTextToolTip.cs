using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;

namespace TrayTerminal.App.Controls;

/// <summary>Shows a TextBlock's tooltip only while trimming cuts its text off.</summary>
internal static class TrimmedTextToolTip
{
    public static void Attach(TextBlock text)
    {
        text.ToolTipOpening += (_, args) => args.Handled = !IsTrimmed(text);
    }

    private static bool IsTrimmed(TextBlock text)
    {
        // A trimmed TextBlock is only as wide as the text it still shows, which
        // is less than its MaxWidth, so compare against the full text instead.
        var fullText = new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            WpfBrushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return fullText.Width > text.ActualWidth + 0.5;
    }
}
