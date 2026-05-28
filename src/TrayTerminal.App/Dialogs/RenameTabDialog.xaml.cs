using System.Windows;

namespace TrayTerminal.App.Dialogs;

public partial class RenameTabDialog : Window
{
    public RenameTabDialog(string currentTitle)
    {
        InitializeComponent();
        TitleTextBox.Text = currentTitle;
        TitleTextBox.SelectAll();
        TitleTextBox.Focus();
        OkButton.Click += (_, _) => Confirm();
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
