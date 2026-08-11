using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using TrayTerminal.App.Controls;
using TrayTerminal.App.Dialogs;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Terminal;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ControlsButton = System.Windows.Controls.Button;
using DrawingIcon = System.Drawing.Icon;
using MediaBrush = System.Windows.Media.Brush;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WpfDataObject = System.Windows.DataObject;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfCursors = System.Windows.Input.Cursors;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace TrayTerminal.App;

public partial class MainWindow : Window
{
    private const int MaxTerminalTabs = 32;
    private const string IdleStatusText = "当前暂无新状态";
    private static readonly TimeSpan StatusIdleDelay = TimeSpan.FromSeconds(60);
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly IReadOnlyList<TerminalProfile> _profiles;
    private readonly WebView2EnvironmentManager _webView2EnvironmentManager;
    private readonly TerminalAuthorityHost _terminalAuthorityHost;
    private readonly string _tabPresetConfigPath;
    private readonly string _initConfigPath;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayVisibilityMenuItem;
    private readonly DispatcherTimer _statusIdleTimer;
    private Dictionary<string, TabPreset> _presets = new(StringComparer.Ordinal);
    private WpfPoint? _tabDragStartPoint;
    private TabItem? _draggedTab;
    private bool _syncingFontSize;
    private bool _backgroundReloadInProgress;
    private RemoteAccessService? _remoteAccess;
    private bool _forceClose;
    private int _untitledIndex = 1;

    public MainWindow(PortablePaths paths, FileLogger logger)
    {
        InitializeComponent();

        _statusIdleTimer = new DispatcherTimer { Interval = StatusIdleDelay };
        _statusIdleTimer.Tick += (_, _) =>
        {
            _statusIdleTimer.Stop();
            StatusText.Text = IdleStatusText;
        };

        _paths = paths;
        _logger = logger;
        _profiles = TerminalProfileCatalog.Detect(paths);
        _webView2EnvironmentManager = new WebView2EnvironmentManager(paths, logger);
        _terminalAuthorityHost = new TerminalAuthorityHost(
            paths,
            logger,
            _webView2EnvironmentManager);
        _tabPresetConfigPath = Path.Combine(paths.ConfigDirectory, "config.txt");
        _initConfigPath = Path.Combine(paths.ConfigDirectory, "init.txt");
        ReloadTabPresets();
        RemoteSettings.Load(paths);
        (_trayIcon, _trayVisibilityMenuItem) = CreateTrayIcon();

        FontSizeComboBox.ItemsSource = new[] { 12, 14, 16, 18, 20, 22, 24 };
        FontSizeComboBox.SelectedItem = 16;
        FontSizeComboBox.SelectionChanged += (_, _) => ApplyFontSizeToSelectedTab();

        NewTabButton.Click += async (_, _) => await CreateTabFromDialogAsync();
        ReloadBackgroundButton.Click += async (_, _) => await ReloadSelectedBackgroundAsync();
        HideToTrayButton.Click += (_, _) => HideToTray();
        Tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == Tabs)
            {
                SyncFontSizeComboBoxToSelectedTab();
                UpdateReloadBackgroundButtonState();
            }
        };
        Loaded += async (_, _) =>
        {
            try
            {
                // Authority initialization is deliberately before any Host/ConPTY
                // creation. There is no renderer-as-authority fallback.
                await _terminalAuthorityHost.InitializeAsync();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to initialize terminal authority.");
                AppMessageDialog.Info(
                    this,
                    $"无法初始化终端权威状态引擎，程序将退出：{exception.Message}");
                _forceClose = true;
                Close();
                return;
            }

            var initEntries = LoadInitEntries();
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

            if (RemoteSettings.Port > 0 && RemoteSettings.AllowedTabs.Count > 0)
            {
                try
                {
                    _remoteAccess = new RemoteAccessService(
                        FindTerminalPage,
                        _logger,
                        RemoteSettings.Port,
                        RemoteSettings.Token,
                        RemoteSettings.AllowedTabs);
                    await _remoteAccess.StartAsync();
                    _logger.Info(
                        $"Remote access started on port {RemoteSettings.Port}"
                        + (RemoteSettings.Token is not null ? " (token required)" : " (no token)")
                        + (RemoteSettings.AllowedTabs.Count > 0
                            ? $", allowed tabs: {string.Join(", ", RemoteSettings.AllowedTabs)}"
                            : ", no tabs allowed"));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to start remote access service on port {RemoteSettings.Port}.");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AppMessageDialog.Info(
                            this,
                            $"无法在端口 {RemoteSettings.Port} 启动远程访问服务（端口可能被占用），程序将退出。");
                        _forceClose = true;
                        Close();
                    });
                }
            }
            else
            {
                _logger.Info("Remote access is disabled because no tabs are allowed.");
            }
        };
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            _statusIdleTimer.Stop();
            if (_remoteAccess is not null)
            {
                try
                {
                    await _remoteAccess.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Failed to stop remote access during shutdown.");
                }
            }

            foreach (var page in Tabs.Items
                         .Cast<TabItem>()
                         .Select(tab => tab.Content)
                         .OfType<TerminalPage>()
                         .ToArray())
            {
                try
                {
                    await page.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Failed to dispose a terminal during shutdown.");
                }
            }

            try
            {
                await _terminalAuthorityHost.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to dispose the terminal authority host.");
            }

            _trayIcon.Dispose();
        };

        SetStatusText(_profiles.Count == 0
            ? "没有检测到可用终端"
            : $"已检测到 {_profiles.Count} 个终端类型");
    }

    private (NotifyIcon Icon, ToolStripMenuItem VisibilityMenuItem) CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var visibilityMenuItem = new ToolStripMenuItem("隐藏");
        visibilityMenuItem.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(visibilityMenuItem);
        menu.Items.Add("退出", null, (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                _forceClose = true;
                Close();
            });
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

    /// <summary>
    /// Looks up a running terminal page by its display title.
    /// Must be called on the UI thread — it accesses <see cref="Tabs"/>.
    /// </summary>
    private TerminalPage? FindTerminalPage(string name)
    {
        return Tabs.Items
            .Cast<TabItem>()
            .Select(tab => tab.Content)
            .OfType<TerminalPage>()
            .FirstOrDefault(page => page.Title == name);
    }

    private async Task CreateTabFromDialogAsync(bool useDefaults = false)
    {
        if (Tabs.Items.Count >= MaxTerminalTabs)
        {
            AppMessageDialog.Info(this, "最多只能同时创建 32 个终端标签");
            return;
        }

        if (_profiles.Count == 0)
        {
            AppMessageDialog.Info(this, "未找到 CMD 或 PowerShell，无法创建终端");
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
        if (string.IsNullOrWhiteSpace(request.Title)
            || request.Title.Any(char.IsControl))
        {
            _logger.Warn("Skipped a terminal with an empty or control-character title.");
            return;
        }

        if (Tabs.Items.Count >= MaxTerminalTabs)
        {
            _logger.Warn($"Skipped terminal '{request.Title}': 32-tab hard limit reached.");
            return;
        }

        if (FindTerminalPage(request.Title) is not null)
        {
            _logger.Warn($"Skipped duplicate terminal title '{request.Title}'.");
            return;
        }

        ReloadTabPresets();
        _presets.TryGetValue(request.Title, out var preset);

        var profile = request.Profile;
        if (preset?.WorkingDirectory is not null)
        {
            profile = profile with { WorkingDirectory = preset.WorkingDirectory };
            request = request with { Profile = profile };
        }

        var page = new TerminalPage(
            request,
            _paths,
            _logger,
            _webView2EnvironmentManager,
            _terminalAuthorityHost,
            GetSelectedFontSize());
        var initialBackground = await page.SetBackgroundImageAsync(
            ResolveBackgroundImage(request.Title, preset),
            preset?.Cover ?? 0);
        if (!initialBackground.Applied)
        {
            _logger.Warn(
                $"Failed to load initial background for tab '{request.Title}': "
                + initialBackground.ErrorMessage);
        }

        var item = new TabItem
        {
            Header = CreateTabHeader(request.Title, page),
            Content = page
        };
        ConfigureTabDragReorder(item);

        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;

        WirePageEvents(page);

        try
        {
            await page.StartAsync();
            if (preset?.RunCommand is not null)
            {
                await page.SendInputAsync(preset.RunCommand + "\r");
            }
            else if (preset?.FillCommand is not null)
            {
                await page.SendInputAsync(preset.FillCommand);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to start terminal tab.");
            AppMessageDialog.Info(this, $"启动终端失败：{exception.Message}");
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
            try
            {
                var bgPath = preset.BackgroundPath;
                if (!Path.IsPathRooted(bgPath))
                {
                    bgPath = Path.GetFullPath(Path.Combine(_paths.BaseDirectory, bgPath));
                }

                if (File.Exists(bgPath))
                {
                    return bgPath;
                }

                _logger.Warn(
                    $"Configured background for tab '{title}' not found at '{bgPath}' "
                    + $"(raw config value: '{preset.BackgroundPath}'). No background will be shown.");
                return null;
            }
            catch (Exception exception) when (IsConfigurationException(exception))
            {
                _logger.Warn($"Ignored invalid background path for tab '{title}': {preset.BackgroundPath}");
                return null;
            }
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

    private void WirePageEvents(TerminalPage page)
    {
        page.Exited += (_, exitCode) =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatusText($"{page.Title} 已退出，代码 {exitCode}");
                DimTabHeader(page);
            });
        };
        page.RecreateRequested += async (_, _) => await RecreateTabAsync(page);
    }

    private async Task RecreateTabAsync(TerminalPage failedPage)
    {
        if (!failedPage.Session.IsFailed)
        {
            return;
        }

        ReloadTabPresets();
        _presets.TryGetValue(failedPage.Title, out var preset);
        var request = failedPage.Request;
        var profile = request.Profile;
        if (preset?.WorkingDirectory is not null)
        {
            profile = profile with { WorkingDirectory = preset.WorkingDirectory };
            request = request with { Profile = profile };
        }

        var executeRun = preset?.RunCommand is not null
            && AppMessageDialog.Confirm(
                this,
                $"重建后将执行预设命令：\n{preset.RunCommand}\n\n是否继续执行该命令？",
                "执行并重建",
                "仅重建");

        var replacement = new TerminalPage(
            request,
            _paths,
            _logger,
            _webView2EnvironmentManager,
            _terminalAuthorityHost,
            failedPage.TerminalFontSize);
        var background = await replacement.SetBackgroundImageAsync(
            ResolveBackgroundImage(failedPage.Title, preset),
            preset?.Cover ?? 0);
        if (!background.Applied)
        {
            _logger.Warn(
                $"Failed to load background while recreating '{failedPage.Title}': "
                + background.ErrorMessage);
        }

        try
        {
            await replacement.StartAsync();
            if (executeRun && preset?.RunCommand is not null)
            {
                await replacement.SendInputAsync(preset.RunCommand + "\r");
            }
            else if (preset?.RunCommand is null && preset?.FillCommand is not null)
            {
                await replacement.SendInputAsync(preset.FillCommand);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Failed to recreate terminal '{failedPage.Title}'.");
            AppMessageDialog.Info(this, $"重建终端失败：{exception.Message}");
            await replacement.DisposeAsync();
            return;
        }

        var item = Tabs.Items
            .Cast<TabItem>()
            .FirstOrDefault(tab => ReferenceEquals(tab.Content, failedPage));
        if (item is null)
        {
            await replacement.DisposeAsync();
            return;
        }

        WirePageEvents(replacement);
        item.Content = replacement;
        item.Header = CreateTabHeader(replacement.Title, replacement);
        item.Background = null;
        await failedPage.DisposeAsync();
        SetStatusText($"{replacement.Title} 已手动重建");
    }

    private async Task ReloadSelectedBackgroundAsync()
    {
        if (_backgroundReloadInProgress
            || Tabs.SelectedItem is not TabItem { Content: TerminalPage page })
        {
            UpdateReloadBackgroundButtonState();
            return;
        }

        _backgroundReloadInProgress = true;
        UpdateReloadBackgroundButtonState();
        var title = page.Title;
        try
        {
            ReloadTabPresets();
            _presets.TryGetValue(title, out var preset);
            var imagePath = ResolveBackgroundImage(title, preset);
            var result = await page.SetBackgroundImageAsync(
                imagePath,
                preset?.Cover ?? 0);
            if (!result.Applied)
            {
                _logger.Warn(
                    $"Failed to reload background for tab '{title}': "
                    + result.ErrorMessage);
                SetStatusText($"重载“{title}”的壁纸失败：{result.ErrorMessage}");
                return;
            }

            SetStatusText(imagePath is null
                ? $"未找到“{title}”的壁纸，已清除当前壁纸"
                : $"已重载“{title}”的壁纸");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Failed to reload background for tab '{title}'.");
            SetStatusText($"重载“{title}”的壁纸失败：{exception.Message}");
        }
        finally
        {
            _backgroundReloadInProgress = false;
            UpdateReloadBackgroundButtonState();
        }
    }

    private void UpdateReloadBackgroundButtonState()
    {
        ReloadBackgroundButton.IsEnabled = !_backgroundReloadInProgress
            && Tabs.SelectedItem is TabItem { Content: TerminalPage };
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

    private void ConfigureTabDragReorder(TabItem item)
    {
        item.AllowDrop = true;
        item.PreviewMouseLeftButtonDown += OnTabPreviewMouseLeftButtonDown;
        item.PreviewMouseMove += OnTabPreviewMouseMove;
        item.DragOver += OnTabDragOver;
        item.Drop += OnTabDrop;
    }

    private void OnTabPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        _tabDragStartPoint = null;
        _draggedTab = null;

        if (sender is not TabItem tab || FindAncestor<ControlsButton>(args.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _draggedTab = tab;
        _tabDragStartPoint = args.GetPosition(Tabs);
    }

    private void OnTabPreviewMouseMove(object sender, WpfMouseEventArgs args)
    {
        if (_draggedTab is null || _tabDragStartPoint is not { } startPoint || args.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = args.GetPosition(Tabs);
        if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new WpfDataObject(typeof(TabItem), _draggedTab);
        DragDrop.DoDragDrop(_draggedTab, data, WpfDragDropEffects.Move);
        _tabDragStartPoint = null;
        _draggedTab = null;
    }

    private void OnTabDragOver(object sender, WpfDragEventArgs args)
    {
        args.Effects = sender is TabItem target
            && args.Data.GetDataPresent(typeof(TabItem))
            && !ReferenceEquals(args.Data.GetData(typeof(TabItem)), target)
                ? WpfDragDropEffects.Move
                : WpfDragDropEffects.None;
        args.Handled = true;
    }

    private void OnTabDrop(object sender, WpfDragEventArgs args)
    {
        if (sender is not TabItem target || args.Data.GetData(typeof(TabItem)) is not TabItem source)
        {
            return;
        }

        MoveTab(source, target, args.GetPosition(target).X > target.ActualWidth / 2);
        args.Handled = true;
    }

    private void MoveTab(TabItem source, TabItem target, bool insertAfterTarget)
    {
        if (ReferenceEquals(source, target) || !Tabs.Items.Contains(source) || !Tabs.Items.Contains(target))
        {
            return;
        }

        Tabs.Items.Remove(source);
        var targetIndex = Tabs.Items.IndexOf(target);
        var insertIndex = insertAfterTarget ? targetIndex + 1 : targetIndex;
        Tabs.Items.Insert(Math.Clamp(insertIndex, 0, Tabs.Items.Count), source);
        Tabs.SelectedItem = source;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private void DimTabHeader(TerminalPage page)
    {
        var item = Tabs.Items.Cast<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, page));
        if (item is not null)
        {
            item.Background = (MediaBrush)FindResource("MutedTextBrush");
        }
    }

    private async Task RenameTabAsync(TerminalPage page)
    {
        ReloadTabPresets();
        var dialog = new RenameTabDialog(page.Title) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewTitle))
        {
            return;
        }

        var newTitle = dialog.NewTitle.Trim();
        if (newTitle.Any(char.IsControl))
        {
            AppMessageDialog.Info(this, "标签名称不能包含 NUL 或其他控制字符");
            return;
        }
        if (string.Equals(newTitle, page.Title, StringComparison.Ordinal))
        {
            return;
        }

        if (FindTerminalPage(newTitle) is not null)
        {
            AppMessageDialog.Info(this, "标签名称已存在；名称区分大小写且必须唯一");
            return;
        }

        var oldTitle = page.Title;
        _remoteAccess?.DisconnectTerminal(
            oldTitle,
            "Terminal renamed; reconnect with the new encoded URL.");
        page.SetTitle(newTitle);
        _presets.TryGetValue(newTitle, out var preset);
        var backgroundResult = await page.SetBackgroundImageAsync(
            ResolveBackgroundImage(newTitle, preset),
            preset?.Cover ?? 0);
        if (!backgroundResult.Applied)
        {
            _logger.Warn(
                $"Failed to update background after renaming tab to '{newTitle}': "
                + backgroundResult.ErrorMessage);
        }

        var item = Tabs.Items.Cast<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, page));
        if (item is not null)
        {
            item.Header = CreateTabHeader(newTitle, page);
        }
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
                var candidate = Path.Combine(
                    _paths.BackgroundsDirectory,
                    title + extension);
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
        ReloadTabPresets();
        if (page.IsRunning)
        {
            if (!AppMessageDialog.Confirm(this, "该标签的终端仍在运行，是否强制关闭？", "关闭", "取消"))
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

    private List<InitTabEntry> LoadInitEntries()
    {
        try
        {
            var entries = InitConfig.Load(_initConfigPath);
            var accepted = new List<InitTabEntry>(MaxTerminalTabs);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!names.Add(entry.Title))
                {
                    _logger.Warn($"Init: duplicate tab title '{entry.Title}' skipped.");
                    continue;
                }

                if (accepted.Count >= MaxTerminalTabs)
                {
                    _logger.Warn(
                        $"Init: tab '{entry.Title}' skipped because the 32-tab hard limit was reached.");
                    continue;
                }

                accepted.Add(entry);
            }

            return accepted;
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
            _logger.Warn($"Ignored invalid init config '{_initConfigPath}': {exception.Message}");
            return [];
        }
    }

    private void ReloadTabPresets()
    {
        try
        {
            _presets = SanitizePresets(TabPresetConfig.Load(_tabPresetConfigPath));
            if (_presets.Count == 0)
            {
                _logger.Info($"Tab presets reloaded: 0 entries (file may be empty or missing).");
            }
            else
            {
                foreach (var (name, preset) in _presets)
                {
                    _logger.Info(
                        $"Tab preset loaded: '{name}' "
                        + $"(bg={preset.BackgroundPath ?? "<none>"}, cover={preset.Cover}, "
                        + $"cd={preset.WorkingDirectory ?? "<none>"}, run={preset.RunCommand ?? "<none>"}, fill={preset.FillCommand ?? "<none>"})");
                }
            }
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
            _logger.Warn($"Ignored invalid tab preset config '{_tabPresetConfigPath}': {exception.Message}");
            _presets = new Dictionary<string, TabPreset>(StringComparer.Ordinal);
        }
    }

    private Dictionary<string, TabPreset> SanitizePresets(Dictionary<string, TabPreset> loadedPresets)
    {
        var sanitized = new Dictionary<string, TabPreset>(StringComparer.Ordinal);
        foreach (var (name, preset) in loadedPresets)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.Warn("Ignored a tab preset with an empty name.");
                continue;
            }

            var workingDirectory = ResolveConfiguredWorkingDirectory(name, preset.WorkingDirectory);
            var backgroundPath = IsPathSyntaxValid(preset.BackgroundPath)
                ? preset.BackgroundPath
                : null;
            if (preset.BackgroundPath is not null && backgroundPath is null)
            {
                _logger.Warn($"Ignored invalid background path for tab '{name}': {preset.BackgroundPath}");
            }

            sanitized[name] = preset with
            {
                WorkingDirectory = workingDirectory,
                BackgroundPath = backgroundPath
            };
        }

        return sanitized;
    }

    private string? ResolveConfiguredWorkingDirectory(string presetName, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(_paths.BaseDirectory, configuredPath));
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
        }

        _logger.Warn($"Ignored invalid working directory for tab '{presetName}': {configuredPath}");
        return null;
    }

    private static bool IsPathSyntaxValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
            return false;
        }
    }

    private static bool IsConfigurationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;
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

    private void SetStatusText(string text)
    {
        StatusText.Text = text;
        _statusIdleTimer.Stop();
        _statusIdleTimer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_forceClose)
        {
            var choice = AppMessageDialog.ChooseInline(this, "选择操作：", "退出程序", "隐藏到托盘");
            if (choice == 1)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (choice != 0)
            {
                e.Cancel = true;
                return;
            }
        }

        var runningPages = Tabs.Items
            .Cast<TabItem>()
            .Select(tab => tab.Content)
            .OfType<TerminalPage>()
            .Where(page => page.IsRunning)
            .ToList();

        if (runningPages.Count > 0)
        {
            if (!AppMessageDialog.Confirm(this, $"仍有 {runningPages.Count} 个终端在运行，是否强制退出？", "退出", "取消"))
            {
                e.Cancel = true;
                _forceClose = false;
                return;
            }
        }

        foreach (var page in Tabs.Items.Cast<TabItem>().Select(tab => tab.Content).OfType<TerminalPage>())
        {
            page.KillForShutdown();
        }
    }
}
