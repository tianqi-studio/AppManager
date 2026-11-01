using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using AppManager.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppManager.Services;

public sealed record ScanResult(
    IReadOnlyList<AppItem> Apps,
    int DesktopCount,
    int UwpCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 从开始菜单和 AppsFolder 扫描应用。扫描只会在用户主动点击“扫描本机应用”时执行。
/// </summary>
public sealed class AppScanService
{
    private static readonly string[] ExcludedNameKeywords =
    {
        "uninstall", "unins", "卸载", "remove ", "readme", "read me", "帮助文档",
        "release notes", "更新日志", "website", "官网", "documentation", "文档",
        "support center", "faq", "license", "许可协议", "快速入门"
    };

    private static readonly string[] ExcludedTargetKeywords =
    {
        "unins", "uninstall", "\\setup.exe", "\\update.exe", "\\updater.exe", "\\repair.exe"
    };

    public async Task<ScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var apps = new List<AppItem>();

        progress?.Report("正在读取开始菜单…");
        var desktopApps = new List<AppItem>();
        try
        {
            desktopApps = await ScanStartMenuAsync(progress, cancellationToken).ConfigureAwait(true);
            apps.AddRange(desktopApps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("扫描开始菜单失败。", ex);
            warnings.Add("开始菜单扫描失败");
        }

        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("正在读取 Windows 商店应用…");
        var uwpApps = new List<AppItem>();
        try
        {
            uwpApps = await ScanUwpAsync(progress, cancellationToken).ConfigureAwait(true);
            apps.AddRange(uwpApps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("扫描 UWP 应用失败。", ex);
            warnings.Add("Windows 商店应用扫描失败");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var deduped = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.LaunchTarget))
            .GroupBy(a => a.DedupeKey ?? a.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        progress?.Report($"扫描完成，共发现 {deduped.Count} 个应用。");
        LogService.Info($"应用扫描完成：桌面 {desktopApps.Count}，UWP {uwpApps.Count}，去重后 {deduped.Count}。");

        return new ScanResult(deduped, desktopApps.Count, uwpApps.Count, warnings);
    }

    private static async Task<List<AppItem>> ScanStartMenuAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return await RunOnStaThreadAsync(
            () => ScanStartMenuCore(progress, cancellationToken),
            cancellationToken).ConfigureAwait(true);
    }

    private static List<AppItem> ScanStartMenuCore(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startMenuDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var linkFiles = new List<string>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        foreach (var directory in startMenuDirectories)
        {
            try
            {
                linkFiles.AddRange(Directory.EnumerateFiles(directory, "*.lnk", enumerationOptions));
            }
            catch (Exception ex)
            {
                LogService.Error($"枚举开始菜单失败：{directory}", ex);
            }
        }

        linkFiles = linkFiles
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}Startup{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith($"{Path.DirectorySeparatorChar}Startup.lnk", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (Exception ex)
        {
            LogService.Error("创建 WScript.Shell 失败。", ex);
        }

        if (shell is null)
        {
            throw new InvalidOperationException("无法读取 Windows 快捷方式。");
        }

        var currentProcessPath = Environment.ProcessPath;
        var inspected = 0;

        foreach (var linkPath in linkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspected++;

            if (inspected % 12 == 0)
            {
                progress?.Report($"正在解析开始菜单应用… ({inspected}/{linkFiles.Count})");
            }

            var linkName = Path.GetFileNameWithoutExtension(linkPath);
            if (string.IsNullOrWhiteSpace(linkName) || IsExcludedName(linkName))
            {
                continue;
            }

            try
            {
                object? shortcut = null;
                string targetPath;
                string arguments;
                try
                {
                    shortcut = shell.GetType().InvokeMember(
                        "CreateShortcut",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        shell,
                        new object[] { linkPath });

                    if (shortcut is null)
                    {
                        continue;
                    }

                    targetPath = (string)(shortcut.GetType().InvokeMember(
                        "TargetPath",
                        System.Reflection.BindingFlags.GetProperty,
                        null,
                        shortcut,
                        null) ?? string.Empty);
                    arguments = (string)(shortcut.GetType().InvokeMember(
                        "Arguments",
                        System.Reflection.BindingFlags.GetProperty,
                        null,
                        shortcut,
                        null) ?? string.Empty);
                }
                finally
                {
                    if (shortcut is not null && Marshal.IsComObject(shortcut))
                    {
                        Marshal.FinalReleaseComObject(shortcut);
                    }
                }

                targetPath = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                {
                    continue;
                }

                var extension = Path.GetExtension(targetPath);
                if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".com", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsExcludedTarget(targetPath) ||
                    string.Equals(targetPath, currentProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dedupeKey = AppItemFactory.CreateDesktopDedupeKey(targetPath, arguments);
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                result.Add(AppItemFactory.CreateDesktopApp(
                    linkName,
                    linkPath,
                    targetPath,
                    arguments,
                    targetPath));
            }
            catch (Exception ex)
            {
                LogService.Error($"解析快捷方式失败：{linkPath}", ex);
            }
        }

        if (shell is not null && Marshal.IsComObject(shell))
        {
            Marshal.FinalReleaseComObject(shell);
        }

        progress?.Report($"开始菜单中发现 {result.Count} 个应用。");
        return result;
    }

    private static async Task<List<AppItem>> ScanUwpAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var visibleApps = await RunOnStaThreadAsync(
            () => ReadVisibleUwpApps(cancellationToken),
            cancellationToken).ConfigureAwait(true);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report($"正在解析 {visibleApps.Count} 个商店应用图标…");

        var packages = await Task.Run(
            () => ReadPackageIndex(cancellationToken),
            cancellationToken).ConfigureAwait(true);

        var result = new List<AppItem>();
        foreach (var (name, aumid) in visibleApps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exclamation = aumid.IndexOf('!');
            var familyName = exclamation > 0 ? aumid[..exclamation] : string.Empty;
            var appId = exclamation >= 0 && exclamation + 1 < aumid.Length ? aumid[(exclamation + 1)..] : "App";

            packages.TryGetValue(familyName, out var package);
            var displayName = ResolveDisplayName(name, familyName, package?.DisplayName);
            var logoPath = package is null ? null : FindApplicationLogo(package, appId);

            result.Add(AppItemFactory.CreateUwpApp(displayName, aumid, logoPath));
        }

        progress?.Report($"Windows 应用中发现 {result.Count} 个应用。");
        return result;
    }

    /// <summary>AppsFolder 中带“!”的项就是可见的 UWP 应用。</summary>
    private static List<(string Name, string Aumid)> ReadVisibleUwpApps(CancellationToken cancellationToken)
    {
        var result = new List<(string Name, string Aumid)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? shell = null;
        object? folder = null;
        object? items = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application")
                            ?? throw new InvalidOperationException("无法创建 Shell.Application。");
            shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建 Shell.Application。");
            folder = shell.GetType().InvokeMember(
                "NameSpace",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { "shell:AppsFolder" });
            if (folder is null)
            {
                return result;
            }

            items = folder.GetType().InvokeMember(
                "Items",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                folder,
                null);
            if (items is null)
            {
                return result;
            }

            var count = Convert.ToInt32(items.GetType().InvokeMember(
                "Count",
                System.Reflection.BindingFlags.GetProperty,
                null,
                items,
                null));

            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                object? item = null;
                try
                {
                    item = items.GetType().InvokeMember(
                        "Item",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        items,
                        new object[] { i });

                    if (item is null)
                    {
                        continue;
                    }

                    var aumid = (string)(item.GetType().InvokeMember(
                        "Path",
                        System.Reflection.BindingFlags.GetProperty,
                        null,
                        item,
                        null) ?? string.Empty);
                    var name = (string)(item.GetType().InvokeMember(
                        "Name",
                        System.Reflection.BindingFlags.GetProperty,
                        null,
                        item,
                        null) ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(aumid) ||
                        !aumid.Contains('!') ||
                        string.IsNullOrWhiteSpace(name) ||
                        IsExcludedName(name) ||
                        !seen.Add(aumid))
                    {
                        continue;
                    }

                    result.Add((name, aumid));
                }
                catch (Exception ex)
                {
                    LogService.Error("读取 AppsFolder 项失败。", ex);
                }
                finally
                {
                    if (item is not null && Marshal.IsComObject(item))
                    {
                        Marshal.FinalReleaseComObject(item);
                    }
                }
            }
        }
        finally
        {
            if (items is not null && Marshal.IsComObject(items))
            {
                Marshal.FinalReleaseComObject(items);
            }

            if (folder is not null && Marshal.IsComObject(folder))
            {
                Marshal.FinalReleaseComObject(folder);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        return result;
    }

    private sealed record PackageInfo(string InstallPath, string DisplayName, XDocument Manifest);

    private static Dictionary<string, PackageInfo> ReadPackageIndex(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
        var packageManager = new PackageManager();
        foreach (var package in packageManager.FindPackagesForUser(string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                {
                    continue;
                }

                var installPath = package.InstalledLocation?.Path;
                if (string.IsNullOrWhiteSpace(installPath))
                {
                    continue;
                }

                var manifestPath = Path.Combine(installPath, "AppxManifest.xml");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = XDocument.Load(manifestPath);
                var displayName = string.Empty;
                try
                {
                    displayName = package.DisplayName ?? string.Empty;
                }
                catch
                {
                    // 部分系统包的显示名不可读。
                }

                result[package.Id.FamilyName] = new PackageInfo(installPath, displayName, manifest);
            }
            catch (UnauthorizedAccessException)
            {
                // 很多系统应用的清单位于受保护的 WindowsApps 目录，无法读取时直接跳过。
            }
            catch (Exception ex)
            {
                LogService.Error($"读取应用包信息失败：{package.Id.FullName}", ex);
            }
        }

        return result;
    }

    private static string? FindApplicationLogo(PackageInfo package, string appId)
    {
        try
        {
            var application = package.Manifest
                .Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Application" &&
                    string.Equals(e.Attribute("Id")?.Value, appId, StringComparison.OrdinalIgnoreCase));

            application ??= package.Manifest.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application");

            var visualElements = application?
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VisualElements");

            var logo = visualElements?.Attribute("Square44x44Logo")?.Value
                       ?? visualElements?.Attribute("Square150x150Logo")?.Value
                       ?? visualElements?.Attribute("Square310x310Logo")?.Value
                       ?? package.Manifest.Descendants()
                           .FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;

            if (string.IsNullOrWhiteSpace(logo))
            {
                return null;
            }

            logo = logo.Replace('/', Path.DirectorySeparatorChar);
            var exactPath = Path.Combine(package.InstallPath, logo);
            if (File.Exists(exactPath))
            {
                return exactPath;
            }

            var directory = Path.GetDirectoryName(exactPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var stem = Path.GetFileNameWithoutExtension(exactPath);
            var files = Directory.EnumerateFiles(directory, stem + "*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Contains("contrast-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                return null;
            }

            return files
                .OrderBy(f => GetLogoScore(Path.GetFileName(f)))
                .First();
        }
        catch (Exception ex)
        {
            LogService.Error("解析 UWP 应用图标失败。", ex);
            return null;
        }
    }

    private static int GetLogoScore(string fileName)
    {
        // 分数越低越优先。优先使用较高分辨率、带底色的版本。
        if (fileName.Contains("targetsize-256") &&
            !fileName.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Contains("altform-lightunplated", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.Contains("targetsize-128")) return 1;
        if (fileName.Contains("targetsize-96")) return 2;
        if (fileName.Contains("targetsize-80")) return 3;
        if (fileName.Contains("targetsize-64")) return 4;
        if (fileName.Contains("targetsize-48")) return 5;
        if (fileName.Contains("scale-200")) return 6;
        if (fileName.Contains("altform-unplated")) return 7;
        return 10;
    }

    private static string ResolveDisplayName(string visibleName, string familyName, string? packageDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(visibleName) &&
            !visibleName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return visibleName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(packageDisplayName) &&
            !packageDisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return packageDisplayName.Trim();
        }

        var friendly = familyName.Split('_')[0];
        var lastDot = friendly.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < friendly.Length ? friendly[(lastDot + 1)..] : friendly;
    }

    private static bool IsExcludedName(string name)
    {
        // 关键词数组已统一为小写/无大小写概念，直接用 OrdinalIgnoreCase 匹配，
        // 避免每次调用 ToLowerInvariant 产生新的小写字符串。
        return ExcludedNameKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcludedTarget(string targetPath)
    {
        return ExcludedTargetKeywords.Any(keyword => targetPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>WScript.Shell / Shell.Application 是 STA COM，放在专用 STA 线程里执行更稳定。</summary>
    private static Task<T> RunOnStaThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                tcs.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "AppManager.Scanner"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
