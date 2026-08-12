using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TrayTerminal.App.Dialogs;

public partial class AppMessageDialog : Window
{
    public AppMessageDialog(string message, string[] buttonLabels, Window? owner = null, bool inline = false)
        : this(
            message,
            buttonLabels,
            owner,
            inline,
            defaultButtonIndex: buttonLabels.Length - 1,
            cancelButtonIndex: -1)
    {
    }

    private AppMessageDialog(
        string message,
        string[] buttonLabels,
        Window? owner,
        bool inline,
        int defaultButtonIndex,
        int cancelButtonIndex)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }
        ClickedIndex = -1;

        if (inline)
        {
            BuildInlineLayout(
                message,
                buttonLabels,
                defaultButtonIndex,
                cancelButtonIndex);
        }
        else
        {
            BuildStackedLayout(
                message,
                buttonLabels,
                defaultButtonIndex,
                cancelButtonIndex);
        }
    }

    public int ClickedIndex { get; private set; }

    public static bool Confirm(Window owner, string message, string yesLabel = "确定", string noLabel = "取消")
    {
        var dialog = new AppMessageDialog(message, [noLabel, yesLabel], owner);
        return dialog.ShowDialog() == true && dialog.ClickedIndex == 1;
    }

    public static bool ConfirmDestructive(
        Window owner,
        string message,
        string yesLabel = "确定",
        string noLabel = "取消")
    {
        var dialog = new AppMessageDialog(
            message,
            [noLabel, yesLabel],
            owner,
            inline: false,
            defaultButtonIndex: 0,
            cancelButtonIndex: 0);
        return dialog.ShowDialog() == true && dialog.ClickedIndex == 1;
    }

    public static bool ConfirmWithPreview(
        Window owner,
        string message,
        string preview,
        string yesLabel = "确定",
        string noLabel = "取消")
    {
        var dialog = new AppMessageDialog(message, [noLabel, yesLabel], owner, preview);
        return dialog.ShowDialog() == true && dialog.ClickedIndex == 1;
    }

    public static void Info(Window owner, string message)
    {
        var dialog = new AppMessageDialog(message, ["确定"], owner);
        dialog.ShowDialog();
    }

    public static int Choose(Window owner, string message, params string[] options)
    {
        var dialog = new AppMessageDialog(message, options, owner);
        return dialog.ShowDialog() == true ? dialog.ClickedIndex : -1;
    }

    public static int ChooseInline(Window owner, string message, params string[] options)
    {
        var dialog = new AppMessageDialog(message, options, owner, inline: true);
        return dialog.ShowDialog() == true ? dialog.ClickedIndex : -1;
    }

    private AppMessageDialog(string message, string[] buttonLabels, Window owner, string preview)
    {
        InitializeComponent();
        Owner = owner;
        ClickedIndex = -1;
        BuildPreviewLayout(message, preview, buttonLabels);
    }

    private void BuildStackedLayout(
        string message,
        string[] buttonLabels,
        int defaultButtonIndex,
        int cancelButtonIndex)
    {
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 22)
        };
        RootGrid.Children.Add(text);

        var panel = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetRow(panel, 1);
        AddButtons(
            panel,
            buttonLabels,
            defaultButtonIndex,
            cancelButtonIndex);
        RootGrid.Children.Add(panel);
    }

    private void BuildInlineLayout(
        string message,
        string[] buttonLabels,
        int defaultButtonIndex,
        int cancelButtonIndex)
    {
        var panel = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        panel.Children.Add(text);
        AddButtons(
            panel,
            buttonLabels,
            defaultButtonIndex,
            cancelButtonIndex);

        RootGrid.Children.Add(panel);
    }

    private void BuildPreviewLayout(string message, string preview, string[] buttonLabels)
    {
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MaxWidth = 660,
            Margin = new Thickness(0, 0, 0, 12)
        };
        RootGrid.Children.Add(text);

        var previewBox = new WpfTextBox
        {
            Text = preview,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new WpfFontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxWidth = 660,
            MinWidth = 520,
            MaxHeight = 240,
            Margin = new Thickness(0, 0, 0, 18)
        };
        Grid.SetRow(previewBox, 1);
        RootGrid.Children.Add(previewBox);

        var panel = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetRow(panel, 2);
        AddButtons(
            panel,
            buttonLabels,
            defaultButtonIndex: buttonLabels.Length - 1,
            cancelButtonIndex: -1);
        RootGrid.Children.Add(panel);
    }

    private void AddButtons(
        WpfPanel panel,
        string[] buttonLabels,
        int defaultButtonIndex,
        int cancelButtonIndex)
    {
        for (var i = 0; i < buttonLabels.Length; i++)
        {
            var index = i;
            var button = new WpfButton
            {
                Content = buttonLabels[i],
                MinWidth = 82,
                Height = 30,
                Margin = i == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0)
            };
            button.Click += (_, _) =>
            {
                ClickedIndex = index;
                DialogResult = true;
            };
            button.IsDefault = i == defaultButtonIndex;
            button.IsCancel = i == cancelButtonIndex;
            panel.Children.Add(button);
        }
    }
}
