using System.Windows;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Dialogs;

public partial class NewTerminalDialog : Window
{
    public NewTerminalDialog(IReadOnlyList<TerminalProfile> profiles, string defaultTitle)
    {
        InitializeComponent();
        TitleTextBox.Text = defaultTitle;
        ProfileComboBox.ItemsSource = profiles;
        ProfileComboBox.SelectedIndex = 0;
        OkButton.Click += (_, _) => Confirm();
    }

    public NewTerminalRequest? Request { get; private set; }

    private void Confirm()
    {
        if (ProfileComboBox.SelectedItem is not TerminalProfile profile)
        {
            return;
        }

        var title = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = profile.DisplayName;
        }

        Request = new NewTerminalRequest(title, profile, AdminCheckBox.IsChecked == true);
        DialogResult = true;
    }
}
