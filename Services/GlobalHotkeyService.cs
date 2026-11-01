using System.ComponentModel;
using System.Runtime.InteropServices;
using AppManager.Models;
using AppManager.Native;
using AppManager.Services;

namespace AppManager.Services;

/// <summary>使用 RegisterHotKey 注册真正意义上的系统级热键。</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0xA701;
    private NativeMessageWindow? _messageWindow;
    private HotkeyPreset? _registeredPreset;
    private bool _disposed;

    public event EventHandler? Pressed;

    public HotkeyPreset? CurrentPreset => _registeredPreset;

    public static string GetDisplayName(HotkeyPreset preset)
    {
        return preset switch
        {
            HotkeyPreset.ControlR => "Ctrl + R",
            HotkeyPreset.AltR => "Alt + R",
            HotkeyPreset.BareR => "R",
            HotkeyPreset.ControlSpace => "Ctrl + Space",
            _ => "Ctrl + R"
        };
    }

    public bool TryRegister(HotkeyPreset preset, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = string.Empty;

        if (_registeredPreset == preset)
        {
            return true;
        }

        try
        {
            _messageWindow ??= new NativeMessageWindow("Hotkey", WindowProc);
        }
        catch (Exception ex)
        {
            error = "无法初始化全局快捷键。";
            LogService.Error("初始化全局快捷键窗口失败。", ex);
            return false;
        }

        var previous = _registeredPreset;
        if (previous is not null)
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            _registeredPreset = null;
        }

        var (modifiers, virtualKey) = MapHotkey(preset);
        if (NativeMethods.RegisterHotKey(_messageWindow.Handle, HotkeyId, modifiers, virtualKey))
        {
            _registeredPreset = preset;
            LogService.Info($"已注册全局快捷键：{GetDisplayName(preset)}");
            return true;
        }

        var win32Error = Marshal.GetLastWin32Error();
        error = win32Error == 1409
            ? $"快捷键 {GetDisplayName(preset)} 已被其他程序占用。"
            : new Win32Exception(win32Error).Message;

        LogService.Error($"注册全局快捷键失败：{GetDisplayName(preset)}，{error}");

        // 变更失败时恢复原快捷键，避免用户设置丢失后完全没有热键。
        if (previous is not null)
        {
            var (oldModifiers, oldVirtualKey) = MapHotkey(previous.Value);
            if (NativeMethods.RegisterHotKey(_messageWindow.Handle, HotkeyId, oldModifiers, oldVirtualKey))
            {
                _registeredPreset = previous;
            }
            else
            {
                error += " 原快捷键也未能恢复。";
            }
        }

        return false;
    }

    public void Unregister()
    {
        if (_messageWindow is not null && _registeredPreset is not null)
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            _registeredPreset = null;
        }
    }

    private static (uint Modifiers, uint VirtualKey) MapHotkey(HotkeyPreset preset)
    {
        return preset switch
        {
            HotkeyPreset.ControlR => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_R),
            HotkeyPreset.AltR => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_R),
            HotkeyPreset.BareR => (NativeMethods.MOD_NOREPEAT, NativeMethods.VK_R),
            HotkeyPreset.ControlSpace => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE),
            _ => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_R)
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _messageWindow?.Dispose();
        _messageWindow = null;
    }
}
