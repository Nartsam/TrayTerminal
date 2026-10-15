using System.Windows.Threading;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TrayTerminal.App.Dialogs;

internal static class TextBoxSelection
{
    /// <summary>
    /// Makes a double click select the whole text instead of a single word.
    /// </summary>
    public static void SelectAllOnDoubleClick(WpfTextBox textBox)
    {
        textBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2)
            {
                return;
            }

            e.Handled = true;
            textBox.Focus();
            // Run after the TextBox's own word selection, if it still happens.
            textBox.Dispatcher.BeginInvoke(textBox.SelectAll, DispatcherPriority.Input);
        };
    }
}
