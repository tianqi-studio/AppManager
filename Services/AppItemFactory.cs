using AppManager.Models;

namespace AppManager.Services;

/// <summary>构造应用条目并生成稳定的去重键。</summary>
public static class AppItemFactory
{
    public static AppItem CreateDesktopApp(
        string name,
        string launchTarget,
        string? targetForDedupe,
        string? arguments = null,
        string? iconSourceHint = null,
        string source = "Desktop")
    {
        var dedupeKey = CreateDesktopDedupeKey(targetForDedupe ?? launchTarget, arguments);
        return new AppItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(launchTarget) : name.Trim(),
            LaunchTarget = launchTarget,
            Source = source,
            DedupeKey = dedupeKey,
            IconSourceHint = iconSourceHint ?? launchTarget,
            CategoryId = string.Empty
        };
    }

    public static AppItem CreateUwpApp(string name, string aumid, string? logoPath)
    {
        return new AppItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            LaunchTarget = aumid,
            Source = "UWP",
            DedupeKey = "uwp:" + aumid.ToLowerInvariant(),
            IconSourceHint = logoPath,
            CategoryId = string.Empty
        };
    }

    public static string CreateDesktopDedupeKey(string path, string? arguments)
    {
        var normalized = path.Trim().Trim('"').ToLowerInvariant();
        var args = string.IsNullOrWhiteSpace(arguments) ? string.Empty : arguments.Trim().ToLowerInvariant();
        return $"desktop:{normalized}|{args}";
    }
}
