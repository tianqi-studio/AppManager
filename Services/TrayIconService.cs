using System.ComponentModel;
using System.Runtime.InteropServices;
using AppManager.Native;
using AppManager.Services;

namespace AppManager.Services;

/// <summary>Win32 Shell_NotifyIcon 托盘图标，避免引入额外的界面框架依赖。</summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 17;
    private const uint TrayIconId = 1;
    private const uint CommandToggle = 1001;
    private const uint CommandSettings = 1002;
    private const uint CommandExit = 1003;

    private readonly Func<bool> _isWindowVisible;
    private readonly Action _toggleRequested;
    private readonly Action _settingsRequested;
    private readonly Action _exitRequested;
    private readonly NativeMessageWindow _messageWindow;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _disposed;
    private string _hotkeyDisplay = "Ctrl + R";

    public TrayIconService(
        Func<bool> isWindowVisible,
        Action toggleRequested,
        Action settingsRequested,
        Action exitRequested)
    {
        _isWindowVisible = isWindowVisible;
        _toggleRequested = toggleRequested;
        _settingsRequested = settingsRequested;
        _exitRequested = exitRequested;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _messageWindow = new NativeMessageWindow("Tray", WindowProc);
        LoadTrayIcon();
        AddIcon();

        if (!_iconAdded)
        {
            throw new InvalidOperationException("无法在系统托盘中创建图标。");
        }
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        var data = CreateIconData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = 0x00000001; // NIIF_INFO
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>快捷键更改后同步托盘悬停提示。</summary>
    public void UpdateHotkeyDisplay(string displayName)
    {
        if (_disposed || string.IsNullOrWhiteSpace(displayName) || _hotkeyDisplay == displayName)
        {
            return;
        }

        _hotkeyDisplay = displayName;
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        data.uFlags = NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        data.szTip = BuildTooltip();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private string BuildTooltip()
    {
        return $"应用收纳器\n{_hotkeyDisplay} 呼出";
    }

    private void LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        if (File.Exists(iconPath))
        {
            var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
            _iconHandle = NativeMethods.LoadImage(
                IntPtr.Zero,
                iconPath,
                NativeMethods.IMAGE_ICON,
                size,
                size,
                NativeMethods.LR_LOADFROMFILE);

            _ownsIcon = _iconHandle != IntPtr.Zero;
        }

        if (_iconHandle == IntPtr.Zero)
        {
            _iconHandle = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        }
    }

    private void AddIcon()
    {
        var data = CreateIconData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        data.uCallbackMessage = TrayCallbackMessage;
        data.hIcon = _iconHandle;
        data.szTip = BuildTooltip();

        _iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_iconAdded)
        {
            LogService.Error($"创建托盘图标失败，Win32 错误：{Marshal.GetLastWin32Error()}");
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateIconData()
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageWindow.Handle,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMessage)
            {
                case NativeMethods.WM_LBUTTONUP:
                    _toggleRequested();
                    return IntPtr.Zero;
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_CONTEXTMENU:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var toggleLabel = _isWindowVisible() ? "收起收纳器" : "打开收纳器";
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(CommandToggle), toggleLabel);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(CommandSettings), "设置");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(CommandExit), "退出");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_messageWindow.Handle);
            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                _messageWindow.Handle,
                IntPtr.Zero);

            NativeMethods.PostMessage(_messageWindow.Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandToggle:
                    _toggleRequested();
                    break;
                case CommandSettings:
                    _settingsRequested();
                    break;
                case CommandExit:
                    _exitRequested();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("显示托盘菜单失败。", ex);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_iconAdded)
        {
            var data = CreateIconData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        _messageWindow.Dispose();

        if (_ownsIcon && _iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }

        _iconHandle = IntPtr.Zero;
    }
}
