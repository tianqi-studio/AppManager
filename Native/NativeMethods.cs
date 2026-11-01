using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace AppManager.Native;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const uint WS_CAPTION = 0x00C00000; // WS_BORDER | WS_DLGFRAME
    public const uint WS_THICKFRAME = 0x00040000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const int MDT_EFFECTIVE_DPI = 0;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;
    public const int DWMWCP_ROUND = 2;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;
    public const int SM_REMOTESESSION = 0x1000;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_SHOWTIP = 0x00000080;

    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint WM_APP = 0x8000;
    public const uint WM_NULL = 0x0000;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_TASKBARCREATED = 0x0000;

    public const int IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_R = 0x52;
    public const uint VK_SPACE = 0x20;

    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLongPtr32(hWnd, nIndex);
    }

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : SetWindowLongPtr32(hWnd, nIndex, value);
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(
        IntPtr hInst,
        string lpszName,
        int uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>远程桌面会话通常会禁用/降级透明与合成效果。</summary>
    public static bool IsRemoteSession()
    {
        try
        {
            return GetSystemMetrics(SM_REMOTESESSION) != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>按光标所在屏幕计算窗口位置和物理像素尺寸。</summary>
    public static WindowPlacement CalculateWindowPlacement(int logicalWidth, int logicalHeight)
    {
        if (!GetCursorPos(out var point))
        {
            point = new POINT { X = 0, Y = 0 };
        }

        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new WindowPlacement(0, 0, 960, 600, 0, 0, 0, 0);
        }

        var dpiX = 96u;
        var dpiY = 96u;
        try
        {
            GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
        }
        catch
        {
            // Windows 8.1 之前的系统不支持 shcore，退回到 96 DPI。
        }

        if (dpiX == 0)
        {
            dpiX = 96;
        }

        if (dpiY == 0)
        {
            dpiY = 96;
        }

        var scale = Math.Max(0.75, dpiX / 96.0);
        var workWidth = info.rcWork.Right - info.rcWork.Left;
        var workHeight = info.rcWork.Bottom - info.rcWork.Top;

        var width = Math.Min((int)Math.Round(logicalWidth * scale), Math.Max(640, workWidth - 32));
        var height = Math.Min((int)Math.Round(logicalHeight * scale), Math.Max(440, workHeight - 48));
        var margin = (int)Math.Round(24 * scale);

        var left = info.rcWork.Left + (workWidth - width) / 2;
        var top = info.rcWork.Bottom - height - margin;
        var hiddenTop = info.rcWork.Bottom + 16;

        return new WindowPlacement(left, top, width, height, info.rcWork.Bottom, hiddenTop, (int)dpiX, (int)dpiY);
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern int GetRgnBox(IntPtr hRgn, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Windows 11 的 DWM 原生圆角会同时裁切窗口内容与系统背景材质。
    /// 此时再叠加 SetWindowRgn 会形成两套不同的圆角，背景材质会从两者之间露出。
    /// </summary>
    public static bool UsesSystemRoundedCorners =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void ApplyRoundedRegion(IntPtr hwnd, double radiusDip)
    {
        try
        {
            if (!GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var radius = (int)Math.Round(radiusDip * dpi / 96.0);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region == IntPtr.Zero)
            {
                return;
            }

            // SetWindowRgn 成功后由系统接管该区域的释放。
            if (SetWindowRgn(hwnd, region, true) == 0)
            {
                DeleteObject(region);
            }
        }
        catch
        {
            // 圆角裁切失败时保留普通矩形窗口，不影响功能。
        }
    }

    /// <summary>去掉系统非客户区边框样式，让内容铺满整个窗口。</summary>
    public static void RemoveWindowFrame(IntPtr hwnd)
    {
        try
        {
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            var updated = style & ~(long)(WS_CAPTION | WS_THICKFRAME);
            if (updated != style)
            {
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(updated));
            }
        }
        catch
        {
            // 修改样式失败时保留默认窗口框架。
        }
    }

    public static string SetRoundedCorners(IntPtr hwnd, bool enabled)
    {
        try
        {
            var preference = enabled ? DWMWCP_ROUND : 1; // 1 = DWMWCP_DONOTROUND
            var cornerResult = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

            var color = unchecked((int)DWMWA_COLOR_NONE);
            var borderResult = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));

            // Win11 22H2+ 还会绘制 1px 的可见框架边框；置 0 可避免四角出现系统亮边。
            var borderThickness = 0;
            var thicknessResult = DwmSetWindowAttribute(
                hwnd,
                DWMWA_VISIBLE_FRAME_BORDER_THICKNESS,
                ref borderThickness,
                sizeof(int));

            return $"corner=0x{cornerResult:X8}, border=0x{borderResult:X8}, thickness=0x{thicknessResult:X8}";
        }
        catch (Exception ex)
        {
            // 旧版 Windows 上这些属性不可用，忽略即可。
            return ex.Message;
        }
    }

    /// <summary>重新应用 DWM 窗口外观，并强制刷新非客户区，防止边框色被系统背景材质重置。</summary>
    public static string RefreshWindowChrome(IntPtr hwnd, bool rounded)
    {
        RemoveWindowFrame(hwnd);
        var result = SetRoundedCorners(hwnd, rounded);
        try
        {
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch
        {
            // 刷新失败不影响窗口功能。
        }

        return result;
    }

    /// <summary>输出窗口框架诊断信息，便于定位外圈白边来源。</summary>
    public static string DescribeWindowFrame(IntPtr hwnd)
    {
        try
        {
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            var dpi = GetDpiForWindow(hwnd);
            var border = -1;
            DwmGetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, out border, sizeof(int));
            GetWindowRect(hwnd, out var windowRect);
            GetClientRect(hwnd, out var clientRect);

            var regionInfo = "rgn=无";
            var region = CreateRectRgn(0, 0, 0, 0);
            if (region != IntPtr.Zero)
            {
                var regionType = GetWindowRgn(hwnd, region);
                var box = new RECT();
                if (regionType > 0 && GetRgnBox(region, out box) > 0)
                {
                    regionInfo = $"rgn={regionType}:{box.Left},{box.Top},{box.Right},{box.Bottom}";
                }

                DeleteObject(region);
            }

            return $"style=0x{style:X8}, dwmBorder={border}px, " +
                   $"window={windowRect.Right - windowRect.Left}x{windowRect.Bottom - windowRect.Top}, " +
                   $"client={clientRect.Right - clientRect.Left}x{clientRect.Bottom - clientRect.Top}, " +
                   $"{regionInfo}, dpi={dpi}";
        }
        catch (Exception ex)
        {
            return $"读取窗口框架诊断失败：{ex.Message}";
        }
    }

    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        try
        {
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // 旧版系统忽略。
        }
    }

    /// <summary>让 WinUI 窗口不出现在任务栏和 Alt+Tab 中。</summary>
    public static void HideFromTaskSwitcher(IntPtr hwnd)
    {
        try
        {
            var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            var updated = current | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        }
        catch
        {
            // 非关键样式。
        }
    }
}

public readonly record struct WindowPlacement(
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkAreaBottom,
    int HiddenTop,
    int DpiX,
    int DpiY);
