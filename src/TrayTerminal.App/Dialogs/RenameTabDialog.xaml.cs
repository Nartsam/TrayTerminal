using System.Windows;
using TrayTerminal.App.Controls;

namespace TrayTerminal.App.Dialogs;

public partial class RenameTabDialog : Window
{
    public RenameTabDialog(string currentTitle)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        TitleTextBox.Text = currentTitle;
        TextBoxSelection.SelectAllOnDoubleClick(TitleTextBox);
        OkButton.Click += (_, _) => Confirm();
        Loaded += (_, _) =>
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        };
    }

    public string? NewTitle { get; private set; }

    private void Confirm()
    {
        var title = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            ValidationText.Text = "标签名称不能为空";
            return;
        }

        NewTitle = title;
        DialogResult = true;
    }
}
