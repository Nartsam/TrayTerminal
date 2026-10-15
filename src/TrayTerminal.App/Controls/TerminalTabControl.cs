using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace TrayTerminal.App.Controls;

/// <summary>
/// Tab control with a single scrollable header row, an overflow tab list and
/// drag reordering with a drop indicator. Its template lives in App.xaml.
/// </summary>
public sealed class TerminalTabControl : System.Windows.Controls.TabControl
{
    private const int WmMouseHWheel = 0x020E;
    private const double WheelStep = 96;
    // Matches the right margin in the TabItem template.
    private const double TabGap = 4;
    private const double DropIndicatorWidth = 3;
    private const double MinimumRevealDistance = 24;
    private const double EdgeScrollZone = 36;
    // DIP per second at the outer edge; a third of it just inside the zone.
    private const double EdgeScrollMaxSpeed = 1200;
    private static readonly Duration IndicatorMoveDuration = new(TimeSpan.FromMilliseconds(100));

    private readonly DispatcherTimer _edgeScrollTimer;
    private ScrollViewer? _scroll;
    private FrameworkElement? _headerRow;
    private FrameworkElement? _dropIndicator;
    private ButtonBase? _scrollLeftButton;
    private ButtonBase? _scrollRightButton;
    private ButtonBase? _tabListButton;
    private HwndSource? _hwndSource;
    private TabItem? _dragCandidate;
    private WpfPoint _dragStart;
    private TabItem? _draggedTab;
    private int _dropIndex = -1;
    private double _edgeScrollSpeed;
    private long _edgeScrollTimestamp;
    private double? _lastDragX;
    private bool _overflowing;

    public TerminalTabControl()
    {
        _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _edgeScrollTimer.Tick += (_, _) => OnEdgeScrollTick();
        // TabItems inherit AllowDrop from this control, their logical parent, not
        // from the header row they are displayed in. OnDragOver rejects the rest.
        AllowDrop = true;
        Loaded += (_, _) => AttachHorizontalWheelHook();
        Unloaded += (_, _) => DetachHorizontalWheelHook();
    }

    /// <summary>Supplies the text shown for each tab in the overflow tab list.</summary>
    public Func<TabItem, string>? TabTitleProvider { get; set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _scroll = GetTemplateChild("PART_TabScroll") as ScrollViewer;
        _headerRow = GetTemplateChild("PART_HeaderRow") as FrameworkElement;
        _dropIndicator = GetTemplateChild("PART_DropIndicator") as FrameworkElement;
        _scrollLeftButton = GetTemplateChild("PART_ScrollLeft") as ButtonBase;
        _scrollRightButton = GetTemplateChild("PART_ScrollRight") as ButtonBase;
        _tabListButton = GetTemplateChild("PART_TabList") as ButtonBase;
        if (_scroll is null || _headerRow is null)
        {
            return;
        }

        _scroll.ScrollChanged += (_, _) => UpdateOverflowState();
        _scroll.PreviewMouseWheel += OnHeaderMouseWheel;
        _scroll.PreviewMouseLeftButtonDown += OnHeaderPreviewMouseLeftButtonDown;
        _scroll.PreviewMouseMove += OnHeaderPreviewMouseMove;
        _headerRow.SizeChanged += (_, _) =>
        {
            UpdateOverflowState();
            BringSelectedTabIntoView();
        };
        _headerRow.DragOver += OnHeaderDragOver;
        _headerRow.DragLeave += OnHeaderDragLeave;
        _headerRow.Drop += OnHeaderDrop;
        if (_scrollLeftButton is not null)
        {
            _scrollLeftButton.Click += (_, _) => ScrollToNeighborTab(-1);
        }
        if (_scrollRightButton is not null)
        {
            _scrollRightButton.Click += (_, _) => ScrollToNeighborTab(1);
        }
        if (_tabListButton is not null)
        {
            _tabListButton.Click += (_, _) => OpenTabList();
        }
    }

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        // WebView2 forwards Home/End to WPF as accelerator keys, and TabControl
        // would consume them to select the first/last tab. They belong to the
        // terminal. Ctrl+Tab and Ctrl+Shift+Tab keep switching tabs.
        if (e.Key is Key.Home or Key.End)
        {
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnDragOver(WpfDragEventArgs e)
    {
        base.OnDragOver(e);
        // Reached only outside the header row, which handles its own drags.
        if (!e.Handled)
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        // A new or moved tab has no layout yet; scroll once it has been arranged.
        Dispatcher.BeginInvoke(BringSelectedTabIntoView, DispatcherPriority.Loaded);
    }

    private void BringSelectedTabIntoView()
    {
        if (SelectedItem is TabItem { IsLoaded: true } tab)
        {
            tab.BringIntoView();
        }
    }

    private void UpdateOverflowState()
    {
        if (_scroll is null || _headerRow is null)
        {
            return;
        }

        // Compare with the whole row, not the viewport: the buttons take room
        // themselves, and must not keep themselves visible once tabs fit again.
        var overflowing = _scroll.ExtentWidth > _headerRow.ActualWidth + 0.5;
        if (overflowing != _overflowing)
        {
            _overflowing = overflowing;
            var visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in new[] { _scrollLeftButton, _scrollRightButton, _tabListButton })
            {
                if (button is not null)
                {
                    button.Visibility = visibility;
                }
            }
        }

        if (_scrollLeftButton is not null)
        {
            _scrollLeftButton.IsEnabled = _scroll.HorizontalOffset > 0.5;
        }
        if (_scrollRightButton is not null)
        {
            _scrollRightButton.IsEnabled = _scroll.HorizontalOffset < _scroll.ScrollableWidth - 0.5;
        }
    }

    private void ScrollBy(double delta)
    {
        if (_scroll is not null && _scroll.ScrollableWidth > 0)
        {
            _scroll.ScrollToHorizontalOffset(Math.Clamp(
                _scroll.HorizontalOffset + delta,
                0,
                _scroll.ScrollableWidth));
        }
    }

    /// <summary>
    /// Scrolls just far enough to fully reveal the next hidden tab. A tab cut
    /// off by only a sliver does not count, or a click would barely move.
    /// </summary>
    private void ScrollToNeighborTab(int direction)
    {
        if (_scroll is null)
        {
            return;
        }

        var offset = _scroll.HorizontalOffset;
        var viewportRight = offset + _scroll.ViewportWidth;
        var tabs = Items.OfType<TabItem>().Where(tab => tab.IsLoaded).ToList();
        if (direction < 0)
        {
            tabs.Reverse();
        }

        foreach (var tab in tabs)
        {
            var left = GetTabLeft(tab) + offset;
            var right = left + tab.ActualWidth - TabGap;
            if (direction > 0 && right > viewportRight + MinimumRevealDistance)
            {
                ScrollBy(right - viewportRight + TabGap);
                return;
            }
            if (direction < 0 && left < offset - MinimumRevealDistance)
            {
                ScrollBy(left - offset);
                return;
            }
        }
    }

    private double GetTabLeft(TabItem tab)
    {
        return tab.TranslatePoint(new WpfPoint(0, 0), _scroll).X;
    }

    private void OnHeaderMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll is null || _scroll.ScrollableWidth <= 0)
        {
            return;
        }

        ScrollBy(-e.Delta / 120.0 * WheelStep);
        e.Handled = true;
    }

    private void AttachHorizontalWheelHook()
    {
        if (_hwndSource is null && PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwndSource = source;
            source.AddHook(OnWindowMessage);
        }
    }

    private void DetachHorizontalWheelHook()
    {
        _hwndSource?.RemoveHook(OnWindowMessage);
        _hwndSource = null;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF has no horizontal wheel event; touchpads send this for sideways swipes.
        if (msg == WmMouseHWheel && _scroll is { IsMouseOver: true, ScrollableWidth: > 0 })
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            ScrollBy(delta / 120.0 * WheelStep);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OpenTabList()
    {
        if (_tabListButton is null)
        {
            return;
        }

        var menu = new WpfContextMenu
        {
            PlacementTarget = _tabListButton,
            // Right-align with the button, which sits at the window's right edge.
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(
                    new WpfPoint(targetSize.Width - popupSize.Width, targetSize.Height),
                    PopupPrimaryAxis.Horizontal)
            ]
        };
        foreach (var tab in Items.OfType<TabItem>())
        {
            var item = new WpfMenuItem
            {
                // A TextBlock header keeps '_' in titles from becoming access keys.
                Header = new TextBlock
                {
                    Text = TabTitleProvider?.Invoke(tab) ?? tab.Header?.ToString() ?? string.Empty,
                    MaxWidth = 360,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                IsChecked = ReferenceEquals(tab, SelectedItem)
            };
            item.Click += (_, _) =>
            {
                if (Items.Contains(tab))
                {
                    SelectedItem = tab;
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnHeaderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(source) is not null
            || FindAncestor<TabItem>(source) is not { } tab
            || !Items.Contains(tab))
        {
            return;
        }

        _dragCandidate = tab;
        _dragStart = e.GetPosition(this);
    }

    private void OnHeaderPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tab = _dragCandidate;
        _dragCandidate = null;
        _draggedTab = tab;
        tab.Opacity = 0.5;
        try
        {
            DragDrop.DoDragDrop(tab, new WpfDataObject(typeof(TabItem), tab), WpfDragDropEffects.Move);
        }
        finally
        {
            tab.Opacity = 1;
            _draggedTab = null;
            StopDragFeedback();
        }
    }

    private void OnHeaderDragOver(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        // Only reorder tabs dragged from this strip; files and text are rejected.
        if (_scroll is null
            || _draggedTab is null
            || !ReferenceEquals(e.Data.GetData(typeof(TabItem)), _draggedTab))
        {
            e.Effects = WpfDragDropEffects.None;
            StopDragFeedback();
            return;
        }

        var x = e.GetPosition(_scroll).X;
        _lastDragX = x;
        UpdateEdgeScroll(x);
        UpdateDropTarget(x);
        e.Effects = WpfDragDropEffects.Move;
    }

    private void OnHeaderDragLeave(object sender, WpfDragEventArgs e)
    {
        // Leaving for the terminal's own HWND reports no usable position, so hide
        // on every leave. Moving between tabs raises DragOver in the same
        // dispatch, which shows the indicator again before anything renders.
        StopDragFeedback();
    }

    private void OnHeaderDrop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        var tab = _draggedTab;
        var dropIndex = _dropIndex;
        StopDragFeedback();
        if (tab is not null
            && dropIndex >= 0
            && ReferenceEquals(e.Data.GetData(typeof(TabItem)), tab))
        {
            MoveTab(tab, dropIndex);
        }
    }

    private void UpdateEdgeScroll(double x)
    {
        if (_scroll is null || _scroll.ScrollableWidth <= 0)
        {
            _edgeScrollSpeed = 0;
            _edgeScrollTimer.Stop();
            return;
        }

        // Faster the deeper the pointer is in the edge zone, including over the
        // scroll buttons beyond either edge.
        var width = _scroll.ActualWidth;
        var depth = x < EdgeScrollZone
            ? -(EdgeScrollZone - x) / EdgeScrollZone
            : x > width - EdgeScrollZone
                ? (x - (width - EdgeScrollZone)) / EdgeScrollZone
                : 0;
        _edgeScrollSpeed = depth == 0
            ? 0
            : Math.Sign(depth) * (1 + 2 * Math.Min(1, Math.Abs(depth))) / 3 * EdgeScrollMaxSpeed;
        if (_edgeScrollSpeed == 0)
        {
            _edgeScrollTimer.Stop();
        }
        else if (!_edgeScrollTimer.IsEnabled)
        {
            _edgeScrollTimestamp = Stopwatch.GetTimestamp();
            _edgeScrollTimer.Start();
        }
    }

    private void OnEdgeScrollTick()
    {
        if (_draggedTab is null || _edgeScrollSpeed == 0)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        // The drag loop delays timer messages irregularly, so scroll by the
        // time that actually passed rather than a fixed step per tick.
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(0.25, Stopwatch.GetElapsedTime(_edgeScrollTimestamp, now).TotalSeconds);
        _edgeScrollTimestamp = now;
        ScrollBy(_edgeScrollSpeed * elapsed);
        if (_lastDragX is { } x)
        {
            // Tabs moved under a still pointer; follow the new drop position.
            UpdateLayout();
            UpdateDropTarget(x);
        }
    }

    private void UpdateDropTarget(double x)
    {
        if (_draggedTab is null)
        {
            return;
        }

        var tabs = Items.OfType<TabItem>().ToList();
        var sourceIndex = tabs.IndexOf(_draggedTab);
        var index = tabs.Count;
        for (var i = 0; i < tabs.Count; i++)
        {
            var center = GetTabLeft(tabs[i]) + (tabs[i].ActualWidth - TabGap) / 2;
            if (x < center)
            {
                index = i;
                break;
            }
        }

        // Dropping beside itself would not change the order.
        if (sourceIndex < 0 || index == sourceIndex || index == sourceIndex + 1)
        {
            _dropIndex = -1;
            HideDropIndicator();
            return;
        }

        _dropIndex = index;
        var gapCenter = index < tabs.Count
            ? GetTabLeft(tabs[index]) - TabGap / 2
            : GetTabLeft(tabs[^1]) + tabs[^1].ActualWidth - TabGap / 2;
        ShowDropIndicator(gapCenter - DropIndicatorWidth / 2);
    }

    private void ShowDropIndicator(double left)
    {
        if (_dropIndicator is null)
        {
            return;
        }

        if (_dropIndicator.Visibility != Visibility.Visible)
        {
            _dropIndicator.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(_dropIndicator, left);
            _dropIndicator.Visibility = Visibility.Visible;
            return;
        }

        _dropIndicator.BeginAnimation(
            Canvas.LeftProperty,
            new DoubleAnimation(left, IndicatorMoveDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator is not null)
        {
            _dropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void StopDragFeedback()
    {
        _edgeScrollTimer.Stop();
        _edgeScrollSpeed = 0;
        _lastDragX = null;
        _dropIndex = -1;
        HideDropIndicator();
    }

    private void MoveTab(TabItem tab, int insertIndex)
    {
        var sourceIndex = Items.IndexOf(tab);
        if (sourceIndex < 0)
        {
            return;
        }

        var targetIndex = insertIndex > sourceIndex ? insertIndex - 1 : insertIndex;
        if (targetIndex == sourceIndex)
        {
            return;
        }

        Items.RemoveAt(sourceIndex);
        Items.Insert(Math.Clamp(targetIndex, 0, Items.Count), tab);
        SelectedItem = tab;
        Dispatcher.BeginInvoke(() => FlashTab(tab), DispatcherPriority.Loaded);
    }

    private static void FlashTab(TabItem tab)
    {
        if (tab.Template?.FindName("FlashOverlay", tab) is not UIElement overlay)
        {
            return;
        }

        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        overlay.BeginAnimation(OpacityProperty, flash);
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

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
