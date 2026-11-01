using AppManager.Models;
using AppManager.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AppManager;

/// <summary>
/// 应用生命周期入口。启动后只创建隐藏的主面板、托盘图标和全局快捷键，不主动弹出窗口。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LiquidAppManager.SingleInstance";
    private const string ShowRequestEventName = @"Local\LiquidAppManager.ShowRequest";

    private readonly DispatcherQueue? _dispatcherQueue;

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showRequestEvent;
    private RegisteredWaitHandle? _showRequestWait;
    private TrayIconService? _trayIconService;
    private GlobalHotkeyService? _hotkeyService;
    private AppRepository? _repository;
    private ThemeService? _themeService;
    private bool _isExiting;
    private string _lastTrayHotkeyDisplay = string.Empty;

    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!EnsureSingleInstance())
        {
            LogService.Info("检测到已有实例，通知其显示后退出。");
            Exit();
            return;
        }

        try
        {
            StartApplication();
        }
        catch (Exception ex)
        {
            LogService.Error("应用启动失败。", ex);
            ExitApplication();
        }
    }

    private void StartApplication()
    {
        AppPaths.EnsureCreated();
        LogService.Info("应用启动。");

        _repository = AppRepository.Load();
        var scanService = new AppScanService();
        var iconService = new IconService();
        _hotkeyService = new GlobalHotkeyService();
        _themeService = new ThemeService();

        MainWindow = new MainWindow(
            _repository,
            scanService,
            iconService,
            _hotkeyService,
            _themeService);

        _hotkeyService.Pressed += HotkeyService_Pressed;
        var hotkeyError = string.Empty;
        var testMode = string.Equals(
            Environment.GetEnvironmentVariable("APPMANAGER_TEST_MODE"),
            "1",
            StringComparison.Ordinal);
        if (!testMode && !_hotkeyService.TryRegister(_repository.Settings.Hotkey, out hotkeyError))
        {
            LogService.Error($"启动时注册快捷键失败：{hotkeyError}");
        }

        try
        {
            _trayIconService = new TrayIconService(
                () => MainWindow?.IsPanelVisible ?? false,
                () => MainWindow?.TogglePanel(),
                () => MainWindow?.ShowPanel(openSettings: true),
                ExitApplication);
        }
        catch (Exception ex)
        {
            LogService.Error("创建托盘图标失败，程序仍可通过快捷键使用。", ex);
        }

        _repository.Changed += Repository_Changed;
        SyncTrayHotkeyDisplay();

        if (_showRequestEvent is not null)
        {
            _showRequestWait = ThreadPool.RegisterWaitForSingleObject(
                _showRequestEvent,
                ShowRequested,
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        if (!string.IsNullOrWhiteSpace(hotkeyError))
        {
            _trayIconService?.ShowBalloon("快捷键未注册", hotkeyError + " 可在设置中更换快捷键。");
        }

        // 调试和自动化测试可以直接带参数启动。
        var commandLine = Environment.GetCommandLineArgs();
        var openSettings = commandLine.Any(argument =>
            argument.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        var showPanel = openSettings || commandLine.Any(argument =>
            argument.Equals("--show", StringComparison.OrdinalIgnoreCase));
        if (showPanel)
        {
            MainWindow.ShowPanel(openSettings);
        }
    }

    private bool EnsureSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (createdNew)
        {
            _singleInstanceMutex = mutex;
            _showRequestEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ShowRequestEventName);
            return true;
        }

        mutex.Dispose();
        NotifyExistingInstance();
        return false;
    }

    /// <summary>第二个实例通过命名事件请求第一个实例显示面板。</summary>
    private static void NotifyExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowRequestEventName);
                showEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                LogService.Error("通知已有实例失败。", ex);
                return;
            }
        }
    }

    private void ShowRequested(object? state, bool timedOut)
    {
        if (timedOut || _isExiting)
        {
            return;
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_isExiting)
            {
                return;
            }

            MainWindow?.ShowPanel();
        });
    }

    private void HotkeyService_Pressed(object? sender, EventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!_isExiting)
            {
                MainWindow?.TogglePanel();
            }
        });
    }

    private void Repository_Changed(object? sender, EventArgs e)
    {
        SyncTrayHotkeyDisplay();
    }

    /// <summary>让托盘提示始终显示当前实际生效的快捷键。</summary>
    private void SyncTrayHotkeyDisplay()
    {
        if (_trayIconService is null)
        {
            return;
        }

        var preset = _hotkeyService?.CurrentPreset
                     ?? _repository?.Settings.Hotkey
                     ?? HotkeyPreset.ControlR;
        var displayName = GlobalHotkeyService.GetDisplayName(preset);
        if (displayName == _lastTrayHotkeyDisplay)
        {
            return;
        }

        _lastTrayHotkeyDisplay = displayName;
        _trayIconService.UpdateHotkeyDisplay(displayName);
    }

    /// <summary>托盘退出：清理托盘与热键，并允许主窗口真正关闭。</summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        LogService.Info("应用退出。");

        try
        {
            _showRequestWait?.Unregister(null);
            _showRequestWait = null;
        }
        catch (Exception ex)
        {
            LogService.Error("取消显示请求监听失败。", ex);
        }

        try
        {
            _showRequestEvent?.Dispose();
            _showRequestEvent = null;
        }
        catch (Exception ex)
        {
            LogService.Error("释放显示请求事件失败。", ex);
        }

        if (_hotkeyService is not null)
        {
            _hotkeyService.Pressed -= HotkeyService_Pressed;
            try
            {
                _hotkeyService.Dispose();
            }
            catch (Exception ex)
            {
                LogService.Error("释放全局快捷键失败。", ex);
            }

            _hotkeyService = null;
        }

        if (_repository is not null)
        {
            _repository.Changed -= Repository_Changed;
        }

        try
        {
            _themeService?.Dispose();
            _themeService = null;
        }
        catch (Exception ex)
        {
            LogService.Error("释放主题服务失败。", ex);
        }

        try
        {
            _trayIconService?.Dispose();
            _trayIconService = null;
        }
        catch (Exception ex)
        {
            LogService.Error("释放托盘图标失败。", ex);
        }

        try
        {
            _repository?.PersistQuietly();
        }
        catch (Exception ex)
        {
            LogService.Error("退出前保存数据失败。", ex);
        }

        var window = MainWindow;
        MainWindow = null;
        try
        {
            window?.CloseForExit();
        }
        catch (Exception ex)
        {
            LogService.Error("关闭主窗口失败。", ex);
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 进程退出时互斥体会自动释放。
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        try
        {
            Exit();
        }
        catch
        {
            // 退出过程中不再做额外处理。
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogService.Error("未处理异常。", e.Exception);
    }
}
