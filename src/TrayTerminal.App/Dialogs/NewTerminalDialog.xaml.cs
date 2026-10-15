using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTerminal.Shared.Terminal;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace TrayTerminal.App.Dialogs;

public partial class NewTerminalDialog : Window
{
    private readonly List<WpfRadioButton> _profileButtons = [];

    public NewTerminalDialog(
        IReadOnlyList<TerminalProfile> profiles,
        string defaultTitle,
        string? preferredProfileId = null)
    {
        InitializeComponent();
        TitleTextBox.Text = defaultTitle;
        TextBoxSelection.SelectAllOnDoubleClick(TitleTextBox);

        var preferred = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, preferredProfileId, StringComparison.Ordinal))
            ?? profiles.FirstOrDefault();
        foreach (var profile in profiles)
        {
            var button = new WpfRadioButton
            {
                Content = profile.DisplayName,
                Tag = profile,
                GroupName = "Profile",
                Style = (Style)FindResource("SegmentRadioButtonStyle"),
                Margin = new Thickness(_profileButtons.Count == 0 ? 0 : 6, 0, 0, 0),
                IsChecked = ReferenceEquals(profile, preferred)
            };
            _profileButtons.Add(button);
            ProfileButtons.Children.Add(button);
        }

        ProfileButtons.PreviewKeyDown += OnProfileButtonsKeyDown;
        OkButton.Click += (_, _) => Confirm();
        // The default title is rarely kept, so start with it selected for typing.
        Loaded += (_, _) =>
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        };
    }

    public NewTerminalRequest? Request { get; private set; }

    private void OnProfileButtonsKeyDown(object sender, WpfKeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
        if (direction == 0 || _profileButtons.Count == 0)
        {
            return;
        }

        var current = _profileButtons.FindIndex(button => button.IsChecked == true);
        var next = _profileButtons[Math.Clamp(current + direction, 0, _profileButtons.Count - 1)];
        next.IsChecked = true;
        next.Focus();
        e.Handled = true;
    }

    private void Confirm()
    {
        if (_profileButtons.FirstOrDefault(button => button.IsChecked == true)?.Tag
            is not TerminalProfile profile)
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
