using AppManager.Models;
using AppManager.Native;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace AppManager.Services;

/// <summary>
/// 应用 WinUI 3 原生暗色/浅色主题。
/// “跟随系统”模式会读取 Windows 的应用主题，并在系统切换明暗时自动刷新。
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly UISettings? _uiSettings;
    private readonly DispatcherQueue? _dispatcherQueue;

    private Window? _window;
    private FrameworkElement? _root;
    private bool _disposed;

    public ThemeService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        }
        catch (Exception ex)
        {
            LogService.Error("初始化系统主题监听失败，跟随系统模式将只在切换时读取一次。", ex);
        }
    }

    public ThemeMode CurrentMode { get; private set; } = ThemeMode.Dark;

    /// <summary>应用主题。window/root 会被保存，用于系统主题变化时自动刷新。</summary>
    public void Apply(ThemeMode mode, Window window, FrameworkElement root)
    {
        _window = window;
        _root = root;
        CurrentMode = mode;

        root.RequestedTheme = ResolveRequestedTheme(mode);
        ApplyBackdrop(window);
        UpdateDwmAppearance(window, root.ActualTheme);
        LogService.Info(
            $"已应用主题：{GetDisplayName(mode)}（实际 {(root.ActualTheme == ElementTheme.Dark ? "暗色" : "浅色")}）。");
    }

    public static string GetDisplayName(ThemeMode mode)
    {
        return mode switch
        {
            ThemeMode.Light => "浅色",
            ThemeMode.System => "跟随系统",
            _ => "暗色"
        };
    }

    public static string GetShortDisplayName(ThemeMode mode) => GetDisplayName(mode);

    private ElementTheme ResolveRequestedTheme(ThemeMode mode)
    {
        return mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light
        };
    }

    /// <summary>读取系统应用主题。暗色模式下 Foreground 为亮色。</summary>
    private bool IsSystemDark()
    {
        if (_uiSettings is null)
        {
            return true;
        }

        try
        {
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            var luminance = 0.2126 * foreground.R + 0.7152 * foreground.G + 0.0722 * foreground.B;
            return luminance > 128;
        }
        catch (Exception ex)
        {
            LogService.Error("读取系统主题失败，回落到暗色。", ex);
            return true;
        }
    }

    private void UiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_disposed || CurrentMode != ThemeMode.System || _window is null || _root is null)
            {
                return;
            }

            var theme = IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light;
            if (_root.RequestedTheme == theme)
            {
                return;
            }

            _root.RequestedTheme = theme;
            UpdateDwmAppearance(_window, theme);
            LogService.Info($"系统主题已变化，跟随系统模式切换为{(theme == ElementTheme.Dark ? "暗色" : "浅色")}。");
        });
    }

    private static void ApplyBackdrop(Window window)
    {
        try
        {
            // WinUI 3 原生材质；Windows 10 不支持 Mica 时由窗口底色提供实色界面。
            window.SystemBackdrop = MicaController.IsSupported()
                ? new MicaBackdrop()
                : null;
        }
        catch (Exception ex)
        {
            LogService.Error("应用系统背景材质失败。", ex);
            window.SystemBackdrop = null;
        }
    }

    public static void UpdateDwmAppearance(Window window, ElementTheme actualTheme)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var useDark = actualTheme == ElementTheme.Dark;
            NativeMethods.SetDarkMode(hwnd, useDark);
            NativeMethods.SetRoundedCorners(hwnd, true);
        }
        catch (Exception ex)
        {
            LogService.Error("更新窗口 DWM 外观失败。", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_uiSettings is not null)
        {
            _uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
        }

        _window = null;
        _root = null;
    }
}
