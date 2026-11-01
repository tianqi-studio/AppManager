using System.Diagnostics;
using AppManager.Models;

namespace AppManager.Services;

/// <summary>启动桌面应用或 UWP 应用。</summary>
public static class AppLauncher
{
    public static void Launch(AppItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LaunchTarget))
        {
            throw new InvalidOperationException("应用启动路径为空。");
        }

        if (string.Equals(item.Source, "UWP", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{item.LaunchTarget}",
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return;
        }

        if (!File.Exists(item.LaunchTarget))
        {
            throw new FileNotFoundException("应用文件不存在，可能已被移动或卸载。", item.LaunchTarget);
        }

        var desktopStartInfo = new ProcessStartInfo
        {
            FileName = item.LaunchTarget,
            UseShellExecute = true
        };

        var directory = Path.GetDirectoryName(item.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            desktopStartInfo.WorkingDirectory = directory;
        }

        Process.Start(desktopStartInfo);
    }

    public static void OpenContainingFolder(AppItem item)
    {
        var target = item.IconSourceHint;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            target = item.LaunchTarget;
        }

        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            throw new InvalidOperationException("无法定位该应用的文件。");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{target}\"",
            UseShellExecute = true
        });
    }
}
