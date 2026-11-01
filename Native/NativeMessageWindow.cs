using System.ComponentModel;
using System.Runtime.InteropServices;
using AppManager.Services;

namespace AppManager.Native;

/// <summary>
/// 隐藏消息窗口，用于接收托盘和全局热键回调。
/// 使用系统自带的 STATIC 窗口类再子类化，避免在 WinUI 线程上注册自定义窗口类时的兼容性问题。
/// </summary>
internal sealed class NativeMessageWindow : IDisposable
{
    private const int GWLP_WNDPROC = -4;

    private readonly NativeMethods.WndProc _windowProc;
    private readonly NativeMethods.WndProc _callback;
    private readonly IntPtr _moduleHandle;
    private readonly IntPtr _oldWindowProc;
    private bool _disposed;

    public NativeMessageWindow(string purpose, NativeMethods.WndProc callback)
    {
        _callback = callback;
        _windowProc = WindowProc;
        _moduleHandle = NativeMethods.GetModuleHandle(null);

        Handle = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            purpose,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建隐藏消息窗口失败。");
        }

        var newWindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _oldWindowProc = NativeMethods.SetWindowLongPtr(Handle, GWLP_WNDPROC, newWindowProc);
        if (_oldWindowProc == IntPtr.Zero ||
            NativeMethods.GetWindowLongPtr(Handle, GWLP_WNDPROC) != newWindowProc)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.DestroyWindow(Handle);
            throw new Win32Exception(error, "设置隐藏消息窗口过程失败。");
        }
    }

    public IntPtr Handle { get; }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return _callback(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            LogService.Error("隐藏窗口处理消息时发生异常。", ex);
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            if (_oldWindowProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(Handle, GWLP_WNDPROC, _oldWindowProc);
            }

            NativeMethods.DestroyWindow(Handle);
        }

        GC.KeepAlive(_windowProc);
    }
}
