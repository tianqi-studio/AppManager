using System.Collections.ObjectModel;
using System.Diagnostics;
using AppManager.Models;
using AppManager.Native;
using AppManager.Services;
using AppManager.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;

namespace AppManager;

/// <summary>
/// 主面板。窗口默认隐藏，只负责展示、搜索、分类与动画，后台生命周期由 App 管理。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int ShowAnimationMilliseconds = 320;
    private const int HideAnimationMilliseconds = 230;
    private const int AnimationIntervalMilliseconds = 15;
    private const double WindowCornerRadiusDip = 27;

    private readonly AppRepository _repository;
    private readonly AppScanService _scanService;
    private readonly IconService _iconService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly ThemeService _themeService;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Stopwatch _animationStopwatch = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _animationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _chromeRefreshTimer;
    private static readonly bool IsTestMode = string.Equals(
        Environment.GetEnvironmentVariable("APPMANAGER_TEST_MODE"),
        "1",
        StringComparison.Ordinal);
    private readonly bool _autoHideEnabled =
        !IsTestMode &&
        !string.Equals(
            Environment.GetEnvironmentVariable("APPMANAGER_DISABLE_AUTOHIDE"),
            "1",
            StringComparison.Ordinal);

    private AppItem? _firstVisibleApp;
    private string _selectedCategoryId;
    private bool _windowIsVisible;
    private bool _isHiding;
    private bool _isWindowAnimating;
    private bool _animationIsShowing;
    private bool _allowClose;
    private bool _pendingOpenSettings;
    private bool _iconsLoaded;
    private bool _hasBeenActivated;
    private DateTimeOffset _deactivationGuardUntil;
    private int _dialogSuppressionCount;
    private int _externalDialogSuppressionCount;
    private int _modalGeneration;
    private int _modalDesiredWidth = 700;
    private int _appliedWindowWidth;
    private int _appliedWindowHeight;
    private int _animationFromY;
    private int _animationToY;
    private int _animationLeft;
    private int _animationWidth;
    private int _animationHeight;
    private int _animationDurationMs;

    // 渲染缓存：只有数据版本或查询/选中分类真正变化时才重建分类栏和应用列表，
    // 避免每次呼出面板、切换分类时对数百个控件做无意义的全量重建。
    private int _renderedCategoryVersion = -1;
    private string _renderedCategorySelection = string.Empty;
    private int _renderedAppsVersion = -1;
    private string _renderedAppsQuery = string.Empty;
    private string _renderedAppsCategory = string.Empty;

    private static readonly Dictionary<string, Brush?> BrushCache = new(StringComparer.Ordinal);

    public MainWindow(
        AppRepository repository,
        AppScanService scanService,
        IconService iconService,
        GlobalHotkeyService hotkeyService,
        ThemeService themeService)
    {
        _repository = repository;
        _scanService = scanService;
        _iconService = iconService;
        _hotkeyService = hotkeyService;
        _themeService = themeService;

        InitializeComponent();
        Title = "应用收纳器";

        // Win11 由 DWM 负责可见圆角；Win10 没有原生圆角，用 XAML 圆角底衬 + SetWindowRgn 回退。
        var surfaceRadius = NativeMethods.UsesSystemRoundedCorners ? 0 : WindowCornerRadiusDip;
        WindowBackdropTint.CornerRadius = new CornerRadius(surfaceRadius);

        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(AnimationIntervalMilliseconds);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += OnWindowAnimationFrame;

        // 系统背景材质在窗口显示后才完成初始化，可能重置 DWM 边框属性。
        // 延迟一点再刷新一次窗口外观，确保白边被清掉。
        _chromeRefreshTimer = DispatcherQueue.CreateTimer();
        _chromeRefreshTimer.Interval = TimeSpan.FromMilliseconds(400);
        _chromeRefreshTimer.IsRepeating = false;
        _chromeRefreshTimer.Tick += ChromeRefreshTimer_Tick;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = "应用收纳器";
        _appWindow.IsShownInSwitchers = false;

        ConfigureWindow();
        ConfigureEvents();

        _selectedCategoryId = EnsureSelectedCategory();
        _appliedWindowWidth = _repository.Settings.WindowWidth;
        _appliedWindowHeight = _repository.Settings.WindowHeight;
        AppsGrid.ItemsSource = Array.Empty<AppItem>();

        ApplyThemeVisual(_repository.Settings.Theme);
        RefreshAll();
        PositionWindowOffscreen(_repository.Settings.WindowWidth, _repository.Settings.WindowHeight);
        ApplyWindowShape();
        _appWindow.Hide();

        _ = LoadIconsAsync();
    }

    public bool IsPanelVisible => _windowIsVisible && !_isHiding;

    public IntPtr WindowHandle => _hwnd;

    /// <summary>供 App 的托盘和热键使用：面板隐藏时显示，已显示时隐藏。</summary>
    public void TogglePanel()
    {
        if (_windowIsVisible && !_isHiding)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    /// <summary>显示面板；openSettings=true 时在弹出动画结束后打开设置。</summary>
    public void ShowPanel(bool openSettings = false)
    {
        // 刚弹出的短时间内忽略失焦，避免启动时抢不到前台又立刻自动收起。
        _deactivationGuardUntil = DateTimeOffset.UtcNow.AddMilliseconds(ShowAnimationMilliseconds + 300);

        if (_windowIsVisible && !_isHiding)
        {
            if (openSettings)
            {
                OpenSettings();
            }
            else
            {
                ForceWindowToFront();
                FocusSearchBox();
            }

            return;
        }

        if (_windowIsVisible && _isHiding)
        {
            // 收起动画尚未结束时再次呼出，直接从当前位置弹回。
            StopWindowAnimation();
            _isHiding = false;
            _pendingOpenSettings = openSettings;
            ShowWindowFromBottom(startFromCurrentPosition: true);
            return;
        }

        _windowIsVisible = true;
        _isHiding = false;
        _pendingOpenSettings = openSettings;
        SearchBox.Text = string.Empty;
        EnsureSelectedCategory();
        RefreshAll();

        ShowWindowFromBottom(startFromCurrentPosition: false);
    }

    /// <summary>用动画收起面板，不结束进程。</summary>
    public void HidePanel()
    {
        if (!_windowIsVisible || _isHiding)
        {
            return;
        }

        _isHiding = true;
        _pendingOpenSettings = false;
        StopWindowAnimation();

        if (NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            var placement = NativeMethods.CalculateWindowPlacement(
                _repository.Settings.WindowWidth,
                _repository.Settings.WindowHeight);
            StartWindowAnimation(rect.Top, placement.HiddenTop, false, HideAnimationMilliseconds);
        }
        else
        {
            CompleteHide();
        }
    }

    /// <summary>退出程序时允许窗口真正关闭。</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        _windowIsVisible = false;
        _isHiding = false;
        StopWindowAnimation();
        CloseModalImmediate();
        Close();
    }

    /// <summary>应用管理中的文件选择器会抢走焦点，期间暂停“点外部收起”。</summary>
    public void SetExternalDialogOpen(bool isOpen)
    {
        if (isOpen)
        {
            _externalDialogSuppressionCount++;
        }
        else if (_externalDialogSuppressionCount > 0)
        {
            _externalDialogSuppressionCount--;
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
            if (File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }

            NativeMethods.RefreshWindowChrome(_hwnd, true);
            NativeMethods.HideFromTaskSwitcher(_hwnd);
            _appWindow.Closing += AppWindow_Closing;
        }
        catch (Exception ex)
        {
            LogService.Error("配置主窗口失败。", ex);
        }
    }

    private void ConfigureEvents()
    {
        Activated += Window_Activated;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        _repository.Changed += Repository_Changed;

        var escapeAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.Escape
        };
        escapeAccelerator.Invoked += EscapeAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(escapeAccelerator);
    }

    private void PositionWindowOffscreen(int logicalWidth, int logicalHeight)
    {
        var placement = NativeMethods.CalculateWindowPlacement(logicalWidth, logicalHeight);
        _appWindow.MoveAndResize(new RectInt32(
            placement.Left,
            placement.HiddenTop,
            placement.Width,
            placement.Height));
    }

    private void ShowWindowFromBottom(bool startFromCurrentPosition)
    {
        var placement = NativeMethods.CalculateWindowPlacement(
            _repository.Settings.WindowWidth,
            _repository.Settings.WindowHeight);

        var startY = placement.HiddenTop;
        if (startFromCurrentPosition && NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            startY = rect.Top;
            _appWindow.MoveAndResize(new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
        else
        {
            _appWindow.MoveAndResize(new RectInt32(
                placement.Left,
                placement.HiddenTop,
                placement.Width,
                placement.Height));
        }

        _appWindow.Show();
        ForceWindowToFront();
        NativeMethods.RefreshWindowChrome(_hwnd, true);
        ApplyWindowShape();
        ScheduleWindowChromeRefresh();

        if (startFromCurrentPosition)
        {
            StartWindowAnimation(startY, placement.Top, true, ShowAnimationMilliseconds);
        }
        else
        {
            StartWindowAnimation(placement.HiddenTop, placement.Top, true, ShowAnimationMilliseconds);
        }

        FocusSearchBox();
    }

    /// <summary>激活并置顶窗口。快捷键/托盘触发时系统允许切换前台。</summary>
    private void ForceWindowToFront()
    {
        try
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
            }

            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            LogService.Error("置顶主窗口失败。", ex);
        }
    }

    private void StartWindowAnimation(int fromY, int toY, bool showing, int durationMs)
    {
        StopWindowAnimation();

        _animationFromY = fromY;
        _animationToY = toY;
        _animationIsShowing = showing;
        _animationDurationMs = Math.Max(1, durationMs);

        var rect = NativeMethods.GetWindowRect(_hwnd, out var current)
            ? current
            : new NativeMethods.RECT();
        _animationLeft = rect.Left;
        _animationWidth = Math.Max(1, rect.Right - rect.Left);
        _animationHeight = Math.Max(1, rect.Bottom - rect.Top);

        _isWindowAnimating = true;
        _animationStopwatch.Restart();
        _animationTimer.Start();
    }

    private void OnWindowAnimationFrame(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_isWindowAnimating)
        {
            _animationTimer.Stop();
            return;
        }

        var progress = Math.Clamp(
            _animationStopwatch.Elapsed.TotalMilliseconds / _animationDurationMs,
            0,
            1);

        var eased = _animationIsShowing
            ? 1 - Math.Pow(1 - progress, 3)
            : progress * progress;

        var y = (int)Math.Round(_animationFromY + (_animationToY - _animationFromY) * eased);
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            _animationLeft,
            y,
            _animationWidth,
            _animationHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (progress < 1)
        {
            return;
        }

        StopWindowAnimation();
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            _animationLeft,
            _animationToY,
            _animationWidth,
            _animationHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (_animationIsShowing)
        {
            if (!_isHiding)
            {
                NativeMethods.SetWindowPos(
                    _hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                FocusSearchBox();
                if (_pendingOpenSettings)
                {
                    _pendingOpenSettings = false;
                    OpenSettings();
                }
            }
        }
        else
        {
            CompleteHide();
        }
    }

    private void StopWindowAnimation()
    {
        _animationTimer.Stop();
        _isWindowAnimating = false;
        _animationStopwatch.Stop();
    }

    private void CompleteHide()
    {
        _appWindow.Hide();
        _windowIsVisible = false;
        _isHiding = false;
        _hasBeenActivated = false;
        CloseModalImmediate();
        _pendingOpenSettings = false;

        if (_repository.Settings.Theme == ThemeMode.System)
        {
            ThemeService.UpdateDwmAppearance(this, RootGrid.ActualTheme);
        }
    }

    private async void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _hasBeenActivated = true;
            ThemeService.UpdateDwmAppearance(this, RootGrid.ActualTheme);
            return;
        }

        if (!_autoHideEnabled)
        {
            return;
        }

        if (!_windowIsVisible || _isHiding || _dialogSuppressionCount > 0 ||
            _externalDialogSuppressionCount > 0)
        {
            return;
        }

        // 从未真正激活过（例如从终端带参数启动被系统拒绝抢前台）时，
        // 不应把“激活失败”误判为点击了窗口外部。
        if (!_hasBeenActivated || DateTimeOffset.UtcNow < _deactivationGuardUntil)
        {
            return;
        }

        // 给菜单、输入法、弹窗一点稳定时间，并排除属于本进程的前台窗口。
        await Task.Delay(140);
        if (!_windowIsVisible || _isHiding || _dialogSuppressionCount > 0 ||
            _externalDialogSuppressionCount > 0 || !_hasBeenActivated ||
            DateTimeOffset.UtcNow < _deactivationGuardUntil)
        {
            return;
        }

        if (IsForegroundWindowOwnedByThisProcess())
        {
            return;
        }

        HidePanel();
    }

    private static bool IsForegroundWindowOwnedByThisProcess()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ThemeService.UpdateDwmAppearance(this, RootGrid.ActualTheme);
        UpdateSearchBorder();
        ScheduleWindowChromeRefresh();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ModalLayer.Visibility == Visibility.Visible)
        {
            CloseModal();
            return;
        }

        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
            FocusSearchBox();
            return;
        }

        if (!_autoHideEnabled)
        {
            return;
        }

        HidePanel();
    }

    private void Repository_Changed(object? sender, EventArgs e)
    {
        if (_repository.Settings.Theme != _themeService.CurrentMode)
        {
            ApplyThemeVisual(_repository.Settings.Theme);
        }

        if (_repository.Settings.WindowWidth != _appliedWindowWidth ||
            _repository.Settings.WindowHeight != _appliedWindowHeight)
        {
            _appliedWindowWidth = _repository.Settings.WindowWidth;
            _appliedWindowHeight = _repository.Settings.WindowHeight;
            ApplyWindowSizeChange();
        }

        _selectedCategoryId = EnsureSelectedCategory();
        RefreshAll();
    }

    /// <summary>设置页修改窗口尺寸后，立即按新尺寸贴着底部重新定位。</summary>
    private void ApplyWindowSizeChange()
    {
        if (!_windowIsVisible || _isHiding || _isWindowAnimating)
        {
            return;
        }

        var placement = NativeMethods.CalculateWindowPlacement(
            _repository.Settings.WindowWidth,
            _repository.Settings.WindowHeight);
        _appWindow.MoveAndResize(new RectInt32(
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height));
        ApplyWindowShape();
    }

    /// <summary>
    /// Win11 让 DWM 原生圆角同时裁切 XAML 内容与系统背景材质；
    /// Win10 没有这套圆角，继续使用窗口区域裁切作为回退。
    /// </summary>
    private void ApplyWindowShape()
    {
        if (NativeMethods.UsesSystemRoundedCorners)
        {
            return;
        }

        NativeMethods.ApplyRoundedRegion(_hwnd, WindowCornerRadiusDip);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeModalCard();
        DispatcherQueue.TryEnqueue(() => ApplyWindowShape());
        ScheduleWindowChromeRefresh();
    }

    /// <summary>延迟刷新窗口外框，避开 WinUI 背景材质的初始化/重置时机。</summary>
    private void ScheduleWindowChromeRefresh()
    {
        _chromeRefreshTimer.Stop();
        _chromeRefreshTimer.Start();
    }

    private void ChromeRefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var chrome = NativeMethods.RefreshWindowChrome(_hwnd, true);
        ApplyWindowShape();
        var shapeMode = NativeMethods.UsesSystemRoundedCorners ? "dwm" : "region";
        LogService.Info($"窗口边框诊断：{chrome}, shape={shapeMode}, {NativeMethods.DescribeWindowFrame(_hwnd)}");
    }

    private void ApplyThemeVisual(ThemeMode mode)
    {
        try
        {
            _themeService.Apply(mode, this, RootGrid);
            ThemeToggleText.Text = ThemeService.GetShortDisplayName(mode);
            ThemeToggleIcon.Glyph = mode switch
            {
                ThemeMode.Light => "\uE706",
                ThemeMode.System => "\uE770",
                _ => "\uE708"
            };
            UpdateSearchBorder();
        }
        catch (Exception ex)
        {
            LogService.Error("切换界面主题失败。", ex);
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var next = _repository.Settings.Theme switch
        {
            ThemeMode.Dark => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.System,
            _ => ThemeMode.Dark
        };
        _repository.SetTheme(next);
    }

    private void RefreshAll()
    {
        RefreshCategoryBar();
        RefreshApps();
    }

    private string EnsureSelectedCategory()
    {
        if (_repository.FindCategory(_repository.Settings.SelectedCategoryId) is null)
        {
            _repository.Settings.SelectedCategoryId = CategoryIds.All;
        }

        _selectedCategoryId = _repository.Settings.SelectedCategoryId;
        return _selectedCategoryId;
    }

    private void RefreshCategoryBar()
    {
        if (_repository.CategoryVersion == _renderedCategoryVersion &&
            string.Equals(_selectedCategoryId, _renderedCategorySelection, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _renderedCategoryVersion = _repository.CategoryVersion;
        _renderedCategorySelection = _selectedCategoryId;

        CategoryPanel.Children.Clear();

        foreach (var category in _repository.Categories.OrderBy(c => c.SortOrder))
        {
            CategoryPanel.Children.Add(CreateCategoryButton(category));
        }

        CategoryPanel.Children.Add(CreateAddCategoryButton());
        CategoryPanel.Children.Add(CreateCategoryMoreButton());
    }

    private Button CreateCategoryButton(AppCategory category)
    {
        var isSelected = category.Id.Equals(_selectedCategoryId, StringComparison.OrdinalIgnoreCase);
        var count = _repository.GetCategoryCount(category.Id);

        var nameText = new TextBlock
        {
            Text = category.Name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var countText = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 10,
            Opacity = 0.72,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(nameText);
        content.Children.Add(countText);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CategoryChipButtonStyle"],
            Content = content,
            Tag = category.Id
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{category.Name}分类，共 {count} 个应用");

        if (isSelected)
        {
            button.Background = GetBrush("CategorySelectedBackgroundBrush");
            button.BorderBrush = GetBrush("CategorySelectedBorderBrush");
            button.Foreground = GetBrush("CategorySelectedForegroundBrush");
            nameText.Foreground = GetBrush("CategorySelectedForegroundBrush");
            countText.Foreground = GetBrush("CategorySelectedForegroundBrush");
        }
        else
        {
            button.Foreground = GetBrush("TextSecondaryBrush");
            nameText.Foreground = GetBrush("TextSecondaryBrush");
            countText.Foreground = GetBrush("TextSecondaryBrush");
        }

        button.Click += CategoryButton_Click;

        if (!category.IsAll)
        {
            button.ContextFlyout = CreateCategoryContextMenu(category);
            ToolTipService.SetToolTip(button, "单击切换分类，右键可重命名或删除");
        }
        else
        {
            ToolTipService.SetToolTip(button, "显示全部应用");
        }

        return button;
    }

    private Button CreateAddCategoryButton()
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CategoryChipButtonStyle"],
            Width = 34,
            Height = 34,
            MinWidth = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = GetBrush("CategoryChipBackgroundBrush"),
            BorderBrush = GetBrush("CategoryChipBorderBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextSecondaryBrush"),
            Content = new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12
            }
        };
        ToolTipService.SetToolTip(button, "新建分类");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "新建分类");
        button.Click += AddCategoryButton_Click;
        return button;
    }

    private Button CreateCategoryMoreButton()
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["CategoryChipButtonStyle"],
            Width = 34,
            Height = 34,
            MinWidth = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = GetBrush("CategoryChipBackgroundBrush"),
            BorderBrush = GetBrush("CategoryChipBorderBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextSecondaryBrush"),
            Content = new FontIcon
            {
                Glyph = "\uE712",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12
            }
        };
        ToolTipService.SetToolTip(button, "管理分类");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "管理分类");

        var menu = new MenuFlyout();
        var addItem = new MenuFlyoutItem { Text = "新建分类" };
        addItem.Click += async (_, _) => await AddCategoryAsync();
        menu.Items.Add(addItem);

        var currentName = _repository.GetCategoryName(_selectedCategoryId);
        var renameItem = new MenuFlyoutItem { Text = $"重命名「{currentName}」" };
        renameItem.IsEnabled = _selectedCategoryId != CategoryIds.All;
        renameItem.Click += async (_, _) => await RenameCurrentCategoryAsync();
        menu.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem { Text = $"删除「{currentName}」" };
        deleteItem.IsEnabled = _selectedCategoryId != CategoryIds.All;
        deleteItem.Click += async (_, _) => await DeleteCurrentCategoryAsync();
        menu.Items.Add(deleteItem);

        button.Flyout = menu;
        return button;
    }

    private MenuFlyout CreateCategoryContextMenu(AppCategory category)
    {
        var menu = new MenuFlyout();

        var renameItem = new MenuFlyoutItem { Text = "重命名分类" };
        renameItem.Click += async (_, _) => await RenameCategoryAsync(category);
        menu.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem { Text = "删除分类" };
        deleteItem.Click += async (_, _) => await DeleteCategoryAsync(category);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string categoryId)
        {
            return;
        }

        if (categoryId.Equals(_selectedCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedCategoryId = categoryId;
        _repository.SetSelectedCategory(categoryId);
        RefreshCategoryBar();
        RefreshApps();
    }

    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        await AddCategoryAsync();
    }

    private async Task AddCategoryAsync()
    {
        var name = await ShowTextInputDialogAsync(
            "新建分类",
            "分类名称",
            string.Empty,
            "例如：设计、办公、学习",
            value => ValidateCategoryName(value));

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var category = _repository.AddCategory(name);
            _selectedCategoryId = category.Id;
            _repository.SetSelectedCategory(category.Id);
            RefreshCategoryBar();
            RefreshApps();
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync("无法新建分类", ex.Message);
        }
    }

    private async Task RenameCurrentCategoryAsync()
    {
        var category = _repository.FindCategory(_selectedCategoryId);
        if (category is null || category.IsAll)
        {
            return;
        }

        await RenameCategoryAsync(category);
    }

    private async Task RenameCategoryAsync(AppCategory category)
    {
        var name = await ShowTextInputDialogAsync(
            $"重命名「{category.Name}」",
            "新的分类名称",
            category.Name,
            "输入新的分类名称",
            value => ValidateCategoryName(value, category.Id));


        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            _repository.RenameCategory(category.Id, name);
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync("无法重命名分类", ex.Message);
        }
    }

    private async Task DeleteCurrentCategoryAsync()
    {
        var category = _repository.FindCategory(_selectedCategoryId);
        if (category is null || category.IsAll)
        {
            return;
        }

        await DeleteCategoryAsync(category);
    }

    private async Task DeleteCategoryAsync(AppCategory category)
    {
        var count = _repository.GetCategoryCount(category.Id);
        var confirmed = await ShowConfirmDialogAsync(
            $"删除分类「{category.Name}」？",
            count == 0
                ? "该分类为空，删除后不可恢复。"
                : $"该分类下的 {count} 个应用会从该分类移除；其中只属于该分类的应用会转移到其他分类，不会从收纳器中删除。",
            "删除");

        if (!confirmed)
        {
            return;
        }

        try
        {
            _repository.DeleteCategory(category.Id);
            _selectedCategoryId = EnsureSelectedCategory();
            RefreshAll();
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync("无法删除分类", ex.Message);
        }
    }

    private string? ValidateCategoryName(string name, string? exceptCategoryId = null)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "分类名称不能为空。";
        }

        if (trimmed.Length > 20)
        {
            return "分类名称不能超过 20 个字符。";
        }

        var duplicate = _repository.Categories.Any(c =>
            !c.Id.Equals(exceptCategoryId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            c.Name.Equals(trimmed, StringComparison.CurrentCultureIgnoreCase));
        return duplicate ? "已经存在同名分类。" : null;
    }

    private void RefreshApps()
    {
        var query = SearchBox.Text.Trim();
        if (_repository.AppsVersion == _renderedAppsVersion &&
            string.Equals(query, _renderedAppsQuery, StringComparison.Ordinal) &&
            string.Equals(_selectedCategoryId, _renderedAppsCategory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _renderedAppsVersion = _repository.AppsVersion;
        _renderedAppsQuery = query;
        _renderedAppsCategory = _selectedCategoryId;

        IEnumerable<AppItem> filtered = _repository.Apps;

        if (!_selectedCategoryId.Equals(CategoryIds.All, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(a => a.IsInCategory(_selectedCategoryId));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(a =>
                a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                a.LaunchTarget.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var result = filtered
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 整表替换 ItemsSource 只触发一次列表刷新，比 ObservableCollection 逐项添加高效得多。
        AppsGrid.ItemsSource = result;
        _firstVisibleApp = result.FirstOrDefault();
        UpdateEmptyState(query, result.Count);
        UpdateStatusText(query, result.Count);
    }

    private void UpdateEmptyState(string query, int visibleCount)
    {
        if (visibleCount > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(query))
        {
            EmptyTitle.Text = "没有找到匹配的应用";
            EmptyDescription.Text = "试试更短的关键词，或者切换一个分类。";
            EmptyActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_repository.Apps.Count > 0 && _selectedCategoryId != CategoryIds.All)
        {
            var categoryName = _repository.GetCategoryName(_selectedCategoryId);
            EmptyTitle.Text = $"「{categoryName}」分类暂无应用";
            EmptyDescription.Text = "在「管理应用」中可以把应用添加到当前分类。";
            EmptyActionButton.Visibility = Visibility.Visible;
            EmptyActionButton.Content = "管理应用";
            return;
        }

        EmptyTitle.Text = "收纳器还是空的";
        EmptyDescription.Text = "在设置中扫描本机应用，或点击下方按钮手动添加。";
        EmptyActionButton.Visibility = Visibility.Visible;
        EmptyActionButton.Content = "打开应用管理";
    }

    private void UpdateStatusText(string query, int visibleCount)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            StatusText.Text = $"找到 {visibleCount} 个应用";
            return;
        }

        if (_selectedCategoryId == CategoryIds.All)
        {
            StatusText.Text = $"共 {visibleCount} 个应用";
        }
        else
        {
            StatusText.Text = $"{_repository.GetCategoryName(_selectedCategoryId)} · {visibleCount} 个应用";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        RefreshApps();
    }

    /// <summary>搜索框为空时显示居中的占位提示，有内容时隐藏。</summary>
    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchBorder();
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchBorder();
    }

    private void UpdateSearchBorder()
    {
        var isFocused = SearchBox.FocusState != FocusState.Unfocused;
        var brush = isFocused
            ? GetBrush("SearchFocusBorderBrush")
            : GetBrush("SearchBarBorderBrush");
        if (brush is not null)
        {
            SearchBar.BorderBrush = brush;
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _firstVisibleApp is not null)
        {
            e.Handled = true;
            await LaunchAppAsync(_firstVisibleApp);
        }
    }

    private async void AppsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppItem app)
        {
            await LaunchAppAsync(app);
        }
    }

    private async Task LaunchAppAsync(AppItem app)
    {
        try
        {
            AppLauncher.Launch(app);
            _repository.MarkLaunched(app.Id);
            HidePanel();
        }
        catch (Exception ex)
        {
            LogService.Error($"启动应用失败：{app.Name}", ex);
            await ShowMessageDialogAsync("无法启动应用", ex.Message);
        }
    }

    private void AppTile_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppItem app)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "打开应用" };
        openItem.Click += async (_, _) => await LaunchAppAsync(app);
        menu.Items.Add(openItem);

        if (!string.Equals(app.Source, "UWP", StringComparison.OrdinalIgnoreCase))
        {
            var folderItem = new MenuFlyoutItem { Text = "打开文件所在位置" };
            folderItem.Click += async (_, _) =>
            {
                try
                {
                    AppLauncher.OpenContainingFolder(app);
                }
                catch (Exception ex)
                {
                    await ShowMessageDialogAsync("无法定位文件", ex.Message);
                }
            };
            menu.Items.Add(folderItem);
        }

        var categories = _repository.GetAssignableCategories();
        if (categories.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var memberCount = categories.Count(category => app.IsInCategory(category.Id));
            var categoryMenuItem = new MenuFlyoutSubItem
            {
                Text = memberCount > 1 ? $"所属分类（{memberCount}）" : "所属分类"
            };

            foreach (var category in categories)
            {
                var wasMember = app.IsInCategory(category.Id);
                var isOnlyCategory = wasMember && memberCount == 1;
                var categoryItem = new ToggleMenuFlyoutItem
                {
                    Text = category.Name,
                    IsChecked = wasMember,
                    IsEnabled = !isOnlyCategory
                };

                if (isOnlyCategory)
                {
                    ToolTipService.SetToolTip(categoryItem, "应用至少需要属于一个分类");
                }

                categoryItem.Click += (_, _) =>
                {
                    _repository.SetAppCategoryMembership(app.Id, category.Id, !wasMember);
                };
                categoryMenuItem.Items.Add(categoryItem);
            }

            menu.Items.Add(categoryMenuItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var removeItem = new MenuFlyoutItem { Text = "从收纳器中移除" };
        removeItem.Click += (_, _) => _repository.RemoveApp(app.Id);
        menu.Items.Add(removeItem);

        menu.ShowAt(element, new FlyoutShowOptions
        {
            Position = e.GetPosition(element)
        });
    }

    private void EmptyActionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAppsManager();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void ManageAppsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAppsManager();
    }

    private void OpenSettings()
    {
        if (!_windowIsVisible)
        {
            ShowPanel(openSettings: true);
            return;
        }

        var view = new SettingsView(_repository, _hotkeyService, _scanService, _iconService);
        ShowModal(view, "设置", 700);
    }

    private void OpenAppsManager()
    {
        if (!_windowIsVisible)
        {
            ShowPanel();
            return;
        }

        var view = new ManageAppsView(
            _repository,
            _iconService,
            _hwnd,
            SetExternalDialogOpen);
        ShowModal(view, "应用管理", 840);
    }

    private void ShowModal(FrameworkElement content, string title, double desiredWidth)
    {
        _modalGeneration++;
        _modalDesiredWidth = (int)desiredWidth;
        ModalTitleText.Text = title;
        ModalContentHost.Content = content;
        ModalLayer.Opacity = 0;
        ModalLayer.Visibility = Visibility.Visible;
        ResizeModalCard();

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, ModalLayer);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Begin();
    }

    /// <summary>窗口尺寸变化时同步调整浮层大小，避免小窗口下内容溢出。</summary>
    private void ResizeModalCard()
    {
        if (ModalLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var availableWidth = Math.Max(520, RootGrid.ActualWidth - 64);
        var availableHeight = Math.Max(400, RootGrid.ActualHeight - 64);
        ModalCard.Width = Math.Min(_modalDesiredWidth, availableWidth);
        ModalCard.Height = Math.Min(530, availableHeight);
    }

    private void CloseModal()
    {
        if (ModalLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var generation = ++_modalGeneration;
        var fadeOut = new DoubleAnimation
        {
            From = ModalLayer.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, ModalLayer);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeOut);
        storyboard.Completed += (_, _) =>
        {
            if (generation == _modalGeneration)
            {
                ModalLayer.Visibility = Visibility.Collapsed;
                ModalLayer.Opacity = 1;
                ModalContentHost.Content = null;
            }
        };
        storyboard.Begin();
    }

    private void CloseModalImmediate()
    {
        _modalGeneration++;
        ModalLayer.Visibility = Visibility.Collapsed;
        ModalLayer.Opacity = 1;
        ModalContentHost.Content = null;
    }

    private void ModalCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        FocusSearchBox();
    }

    private void ModalScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseModal();
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        if (!_windowIsVisible)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_windowIsVisible)
            {
                return;
            }

            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
        });
    }

    private async Task<string?> ShowTextInputDialogAsync(
        string title,
        string fieldLabel,
        string initialValue,
        string placeholder,
        Func<string, string?> validator)
    {
        var input = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = GetBrush("FieldBackgroundBrush"),
            BorderBrush = GetBrush("FieldBorderBrush")
        };
        var label = new TextBlock
        {
            Text = fieldLabel,
            FontSize = 12,
            Foreground = GetBrush("TextSecondaryBrush")
        };
        var error = new TextBlock
        {
            FontSize = 12,
            Foreground = GetBrush("DangerBrush"),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(error);

        string? result = null;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var validationError = validator(input.Text);
            if (validationError is not null)
            {
                args.Cancel = true;
                error.Text = validationError;
                error.Visibility = Visibility.Visible;
                input.Focus(FocusState.Programmatic);
                return;
            }

            result = input.Text.Trim();
        };

        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };

        var dialogResult = await ShowDialogAsync(dialog);
        return dialogResult == ContentDialogResult.Primary ? result : null;
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetBrush("TextSecondaryBrush")
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = GetBrush("TextSecondaryBrush")
                },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close
            };
            await ShowDialogAsync(dialog);
        }
        catch (Exception ex)
        {
            LogService.Error("显示消息对话框失败。", ex);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialogSuppressionCount++;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogSuppressionCount--;
        }
    }

    private static Brush? GetBrush(string key)
    {
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : null;
        BrushCache[key] = brush;
        return brush;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HidePanel();
    }

    private async Task LoadIconsAsync()
    {
        if (_iconsLoaded)
        {
            return;
        }

        _iconsLoaded = true;
        await _iconService.LoadAllAsync(_repository.Apps);
        _repository.PersistQuietly();
    }
}
