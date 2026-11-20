using System.Diagnostics;
using AppManager.Models;
using AppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppManager.Views;

/// <summary>设置页：主题、全局快捷键、窗口尺寸、应用扫描与数据目录。</summary>
public sealed partial class SettingsView : UserControl
{
    private readonly AppRepository _repository;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly AppScanService _scanService;
    private readonly IconService _iconService;

    /// <summary>
    /// 下拉框顺序与枚举值不再一一对应（BareR 已停用但仍保留在枚举中兼容旧数据），
    /// 因此用显式数组映射，避免 (int) 强转导致选中项错位。
    /// </summary>
    private static readonly HotkeyPreset[] HotkeyOptions =
    {
        HotkeyPreset.ControlR,
        HotkeyPreset.AltR,
        HotkeyPreset.ControlSpace
    };

    private bool _syncingControls;
    private CancellationTokenSource? _scanCts;

    public SettingsView(
        AppRepository repository,
        GlobalHotkeyService hotkeyService,
        AppScanService scanService,
        IconService iconService)
    {
        _repository = repository;
        _hotkeyService = hotkeyService;
        _scanService = scanService;
        _iconService = iconService;

        InitializeComponent();
        SyncControls();
        UpdateHotkeyStatus();
        DataPathText.Text = AppPaths.DataFile;

        Unloaded += SettingsView_Unloaded;
    }

    /// <summary>把持久化设置映射到界面控件，期间忽略控件事件。</summary>
    private void SyncControls()
    {
        _syncingControls = true;
        try
        {
            ThemeComboBox.SelectedIndex = _repository.Settings.Theme switch
            {
                ThemeMode.Light => 1,
                ThemeMode.System => 2,
                _ => 0
            };
            HotkeyComboBox.SelectedIndex = ToHotkeyIndex(_repository.Settings.Hotkey);
            SizeComboBox.SelectedIndex = FindSizeIndex(
                _repository.Settings.WindowWidth,
                _repository.Settings.WindowHeight);
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private static int FindSizeIndex(int width, int height)
    {
        return (width, height) switch
        {
            (760, 500) => 0,
            (900, 560) => 1,
            (1200, 720) => 3,
            _ => 2
        };
    }

    /// <summary>旧数据可能仍保存已停用的裸 R 预设，统一按 Ctrl+R 处理。</summary>
    private static HotkeyPreset NormalizeHotkey(HotkeyPreset preset)
    {
        return preset == HotkeyPreset.BareR ? HotkeyPreset.ControlR : preset;
    }

    private static int ToHotkeyIndex(HotkeyPreset preset)
    {
        var index = Array.IndexOf(HotkeyOptions, NormalizeHotkey(preset));
        return index >= 0 ? index : 0;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls)
        {
            return;
        }

        var mode = ThemeComboBox.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.System,
            _ => ThemeMode.Dark
        };
        if (_repository.Settings.Theme != mode)
        {
            _repository.SetTheme(mode);
        }
    }

    private void HotkeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls)
        {
            return;
        }

        if (HotkeyComboBox.SelectedIndex < 0 || HotkeyComboBox.SelectedIndex >= HotkeyOptions.Length)
        {
            return;
        }

        var preset = HotkeyOptions[HotkeyComboBox.SelectedIndex];
        var alreadyRegistered =
            NormalizeHotkey(_repository.Settings.Hotkey) == preset &&
            _hotkeyService.CurrentPreset == preset;
        if (alreadyRegistered)
        {
            UpdateHotkeyStatus();
            return;
        }

        if (_hotkeyService.TryRegister(preset, out var error))
        {
            _repository.SetHotkey(preset);
            UpdateHotkeyStatus();
            return;
        }

        // 注册失败时恢复为当前实际设置，避免界面显示与实际热键不一致。
        _syncingControls = true;
        HotkeyComboBox.SelectedIndex = ToHotkeyIndex(_repository.Settings.Hotkey);
        _syncingControls = false;

        HotkeyStatusText.Text = error;
        HotkeyStatusText.Foreground = GetBrush("DangerBrush");
    }

    private void UpdateHotkeyStatus()
    {
        var preset = _hotkeyService.CurrentPreset ?? _repository.Settings.Hotkey;
        HotkeyStatusText.Text = $"当前生效：{GlobalHotkeyService.GetDisplayName(preset)}";
        HotkeyStatusText.Foreground = GetBrush("TextMutedBrush");
    }

    private void SizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || SizeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        var parts = tag.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var width) &&
            int.TryParse(parts[1], out var height))
        {
            _repository.SetWindowSize(width, height);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null)
        {
            return;
        }

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        ScanButton.IsEnabled = false;
        CancelScanButton.Visibility = Visibility.Visible;
        ScanProgressBar.Visibility = Visibility.Visible;
        ScanInfoBar.IsOpen = false;
        ScanDetailText.Text = string.Empty;
        ScanStatusText.Text = "准备扫描…";
        ScanStatusText.Foreground = GetBrush("TextSecondaryBrush");

        var progress = new Progress<string>(message => ScanStatusText.Text = message);

        try
        {
            var result = await _scanService.ScanAsync(progress, token);
            token.ThrowIfCancellationRequested();

            ScanStatusText.Text = "正在写入应用列表…";
            var added = _repository.AddApps(result.Apps);

            ScanStatusText.Text = "正在准备应用图标…";
            await _iconService.LoadAllAsync(_repository.Apps, token);
            _repository.PersistQuietly();

            var warnings = result.Warnings.Count > 0
                ? "，部分内容未能读取：" + string.Join("、", result.Warnings)
                : string.Empty;
            ScanDetailText.Text =
                $"开始菜单 {result.DesktopCount} 个 · 商店应用 {result.UwpCount} 个{warnings}";

            ScanStatusText.Text = added > 0
                ? $"扫描完成：发现 {result.Apps.Count} 个应用，新增 {added} 个。"
                : $"扫描完成：发现 {result.Apps.Count} 个应用，都已经在收纳器中。";

            ScanInfoBar.Severity = result.Warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ScanInfoBar.Title = "扫描完成";
            ScanInfoBar.Message = result.Warnings.Count > 0
                ? "部分应用未能读取，可以稍后再次扫描。"
                : "应用已按名称自动归类，可在「管理应用」中调整。";
            ScanInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            LogService.Error("扫描本机应用失败。", ex);
            ScanStatusText.Text = "扫描失败。";
            ScanInfoBar.Severity = InfoBarSeverity.Error;
            ScanInfoBar.Title = "扫描失败";
            ScanInfoBar.Message = ex.Message;
            ScanInfoBar.IsOpen = true;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开数据目录失败。", ex);
            ShowDataMessage("无法打开数据目录", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(AppPaths.LogFile))
            {
                ShowDataMessage("暂无日志", "程序还没有写入运行日志。", InfoBarSeverity.Informational);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{AppPaths.LogFile}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开日志失败。", ex);
            ShowDataMessage("无法打开日志", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowDataMessage(string title, string message, InfoBarSeverity severity)
    {
        ScanInfoBar.Severity = severity;
        ScanInfoBar.Title = title;
        ScanInfoBar.Message = message;
        ScanInfoBar.IsOpen = true;
    }

    private static Brush GetBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
    }
}
