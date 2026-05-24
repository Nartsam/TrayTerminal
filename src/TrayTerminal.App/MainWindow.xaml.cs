using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TrayTerminal.App.Controls;
using TrayTerminal.App.Dialogs;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Terminal;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ControlsButton = System.Windows.Controls.Button;
using DrawingIcon = System.Drawing.Icon;
using MediaBrush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
using WpfCursors = System.Windows.Input.Cursors;

namespace TrayTerminal.App;

public partial class MainWindow : Window
{
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly IReadOnlyList<TerminalProfile> _profiles;
    private readonly Dictionary<string, TabPreset> _presets;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayVisibilityMenuItem;
    private bool _syncingFontSize;
    private int _untitledIndex = 1;

    public MainWindow(PortablePaths paths, FileLogger logger)
    {
        InitializeComponent();

        _paths = paths;
        _logger = logger;
        _profiles = TerminalProfileCatalog.Detect(paths);
        _presets = TabPresetConfig.Load(Path.Combine(paths.ConfigDirectory, "config.txt"));
        (_trayIcon, _trayVisibilityMenuItem) = CreateTrayIcon();

        FontSizeComboBox.ItemsSource = new[] { 12, 14, 16, 18, 20, 22, 24 };
        FontSizeComboBox.SelectedItem = 16;
        FontSizeComboBox.SelectionChanged += (_, _) => ApplyFontSizeToSelectedTab();

        NewTabButton.Click += async (_, _) => await CreateTabFromDialogAsync();
        HideToTrayButton.Click += (_, _) => HideToTray();
        Tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == Tabs)
            {
                SyncFontSizeComboBoxToSelectedTab();
            }
        };
        Loaded += async (_, _) =>
        {
            var initEntries = InitConfig.Load(Path.Combine(paths.ConfigDirectory, "init.txt"));
            if (initEntries.Count > 0)
            {
                foreach (var entry in initEntries)
                {
                    await CreateTabFromInitAsync(entry);
                }
            }
            else if (Tabs.Items.Count == 0)
            {
                await CreateTabFromDialogAsync(useDefaults: true);
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => _trayIcon.Dispose();

        StatusText.Text = _profiles.Count == 0
            ? "没有检测到可用终端。"
            : $"已检测到 {_profiles.Count} 个终端类型。";
    }

    private (NotifyIcon Icon, ToolStripMenuItem VisibilityMenuItem) CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var visibilityMenuItem = new ToolStripMenuItem("隐藏");
        visibilityMenuItem.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(visibilityMenuItem);
        menu.Items.Add("退出", null, (_, _) =>
        {
            Dispatcher.Invoke(Close);
        });

        var processPath = Environment.ProcessPath;
        var appIcon = File.Exists(_paths.AppIconPath)
            ? new DrawingIcon(_paths.AppIconPath)
            : !string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)
            ? DrawingIcon.ExtractAssociatedIcon(processPath!)
            : null;

        var icon = new NotifyIcon
        {
            Text = "TrayTerminal",
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        menu.Opening += (_, _) => UpdateTrayVisibilityMenuText();
        icon.DoubleClick += (_, _) => ToggleWindowVisibility();
        return (icon, visibilityMenuItem);
    }

    private async Task CreateTabFromDialogAsync(bool useDefaults = false)
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show("未找到 CMD 或 PowerShell，无法创建终端。", "TrayTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewTerminalRequest request;
        if (useDefaults)
        {
            var profile = _profiles.First();
            request = new NewTerminalRequest($"终端 {_untitledIndex++}", profile, false);
        }
        else
        {
            var dialog = new NewTerminalDialog(_profiles, $"终端 {_untitledIndex++}") { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Request is null)
            {
                return;
            }

            request = dialog.Request;
        }

        await CreateTabCoreAsync(request);
    }

    private async Task CreateTabFromInitAsync(InitTabEntry entry)
    {
        var profile = FindProfileByType(entry.ProfileType);
        if (profile is null)
        {
            _logger.Info($"Init: profile '{entry.ProfileType}' not found, skipping tab '{entry.Title}'.");
            return;
        }

        var request = new NewTerminalRequest(entry.Title, profile, entry.RunAsAdministrator);
        await CreateTabCoreAsync(request);
    }

    private async Task CreateTabCoreAsync(NewTerminalRequest request)
    {
        _presets.TryGetValue(request.Title, out var preset);

        var profile = request.Profile;
        if (preset?.WorkingDirectory is not null)
        {
            profile = profile with { WorkingDirectory = preset.WorkingDirectory };
            request = request with { Profile = profile };
        }

        var page = new TerminalPage(request, _paths, _logger, GetSelectedFontSize());
        page.SetBackgroundImage(ResolveBackgroundImage(request.Title, preset));
        var item = new TabItem
        {
            Header = CreateTabHeader(request.Title, page),
            Content = page
        };

        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;

        page.Exited += (_, exitCode) =>
        {
            Dispatcher.Invoke(() => StatusText.Text = $"{request.Title} 已退出，代码 {exitCode}。");
        };

        try
        {
            await page.StartAsync();
            if (preset?.RunCommand is not null)
            {
                await page.SendInputAsync(preset.RunCommand + "\r");
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to start terminal tab.");
            MessageBox.Show(exception.Message, "启动终端失败", MessageBoxButton.OK, MessageBoxImage.Error);
            await page.DisposeAsync();
            Tabs.Items.Remove(item);
        }
    }

    private TerminalProfile? FindProfileByType(string profileType)
    {
        var match = _profiles.FirstOrDefault(p =>
            string.Equals(p.Id, profileType, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        match = _profiles.FirstOrDefault(p =>
            p.Id.Contains(profileType, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        return _profiles.FirstOrDefault(p =>
            p.DisplayName.Contains(profileType, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveBackgroundImage(string title, TabPreset? preset)
    {
        if (preset?.BackgroundPath is not null)
        {
            var bgPath = preset.BackgroundPath;
            if (!Path.IsPathRooted(bgPath))
            {
                bgPath = Path.GetFullPath(Path.Combine(_paths.BaseDirectory, bgPath));
            }
            return File.Exists(bgPath) ? bgPath : null;
        }

        return FindBackgroundImagePath(title);
    }

    private int GetSelectedFontSize()
    {
        return FontSizeComboBox.SelectedItem is int fontSize ? fontSize : 16;
    }

    private void ApplyFontSizeToSelectedTab()
    {
        if (_syncingFontSize)
        {
            return;
        }

        if (Tabs.SelectedItem is TabItem { Content: TerminalPage page })
        {
            page.SetFontSize(GetSelectedFontSize());
        }
    }

    private void SyncFontSizeComboBoxToSelectedTab()
    {
        if (Tabs.SelectedItem is not TabItem { Content: TerminalPage page })
        {
            return;
        }

        _syncingFontSize = true;
        try
        {
            FontSizeComboBox.SelectedItem = page.TerminalFontSize;
        }
        finally
        {
            _syncingFontSize = false;
        }
    }

    private FrameworkElement CreateTabHeader(string title, TerminalPage page)
    {
        var panel = new DockPanel { LastChildFill = false, MinWidth = 120, Cursor = WpfCursors.Hand };
        var text = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var close = new ControlsButton
        {
            Content = "x",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            ToolTip = "关闭标签"
        };

        panel.MouseLeftButtonDown += async (_, args) =>
        {
            if (args.ClickCount == 2)
            {
                args.Handled = true;
                await RenameTabAsync(page);
            }
        };

        close.Click += async (sender, args) =>
        {
            args.Handled = true;
            await CloseTabAsync(page);
        };

        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(text);
        panel.Children.Add(close);
        return panel;
    }

    private async Task RenameTabAsync(TerminalPage page)
    {
        var dialog = new RenameTabDialog(page.Title) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewTitle))
        {
            return;
        }

        var newTitle = dialog.NewTitle.Trim();
        page.SetTitle(newTitle);
        _presets.TryGetValue(newTitle, out var preset);
        page.SetBackgroundImage(ResolveBackgroundImage(newTitle, preset));

        var item = Tabs.Items.Cast<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, page));
        if (item is not null)
        {
            item.Header = CreateTabHeader(newTitle, page);
        }

        await Task.CompletedTask;
    }

    private string? FindBackgroundImagePath(string title)
    {
        // Background names intentionally follow the visible tab title. Titles that cannot
        // be represented as Windows file names simply mean "no matched background".
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(_paths.BackgroundsDirectory))
        {
            return null;
        }

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            try
            {
                var candidate = _paths.CombineInsideBase("Backgrounds", title + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task CloseTabAsync(TerminalPage page)
    {
        if (page.IsRunning)
        {
            var result = MessageBox.Show(
                "该标签的终端仍在运行。是否强制关闭？",
                "关闭标签",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await page.DisposeAsync();
        var item = Tabs.Items.Cast<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, page));
        if (item is not null)
        {
            Tabs.Items.Remove(item);
        }
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon.Visible = true;
        UpdateTrayVisibilityMenuText();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UpdateTrayVisibilityMenuText();
    }

    private void ToggleWindowVisibility()
    {
        Dispatcher.Invoke(() =>
        {
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                HideToTray();
            }
            else
            {
                RestoreFromTray();
            }
        });
    }

    private void UpdateTrayVisibilityMenuText()
    {
        _trayVisibilityMenuItem.Text = IsVisible && WindowState != WindowState.Minimized ? "隐藏" : "显示";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var runningPages = Tabs.Items
            .Cast<TabItem>()
            .Select(tab => tab.Content)
            .OfType<TerminalPage>()
            .Where(page => page.IsRunning)
            .ToList();

        if (runningPages.Count > 0)
        {
            var result = MessageBox.Show(
                $"仍有 {runningPages.Count} 个终端在运行。是否强制退出 TrayTerminal？",
                "退出 TrayTerminal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        foreach (var page in Tabs.Items.Cast<TabItem>().Select(tab => tab.Content).OfType<TerminalPage>())
        {
            page.KillForShutdown();
        }
    }
}
