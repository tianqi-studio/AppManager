using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace AppManager.Models;

/// <summary>界面主题：固定暗色、固定浅色，或跟随 Windows 系统设置。</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>可在设置中切换的全局快捷键。</summary>
public enum HotkeyPreset
{
    ControlR,
    AltR,
    BareR,
    ControlSpace
}

/// <summary>应用条目。持久化数据中不保存 ImageSource。</summary>
public sealed class AppItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _categoryId = global::AppManager.Models.CategoryIds.Other;
    private List<string> _categoryIds = new();
    private string? _iconPath;
    private ImageSource? _iconSource;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>
    /// Win32 应用为 exe / lnk 路径；UWP 应用为 AUMID。
    /// </summary>
    public string LaunchTarget { get; set; } = string.Empty;

    /// <summary>Desktop / UWP / Manual，用于决定启动方式。</summary>
    public string Source { get; set; } = "Desktop";

    public string? DedupeKey { get; set; }

    /// <summary>用于从 exe / lnk / 包资源中提取图标的原始路径。</summary>
    public string? IconSourceHint { get; set; }

    /// <summary>缓存到本地 AppData 中的 PNG 图标。</summary>
    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    /// <summary>
    /// 旧版单分类字段，保留用于兼容旧数据；新数据以 <see cref="CategoryIds"/> 为准。
    /// </summary>
    public string CategoryId
    {
        get => _categoryId;
        set => SetField(ref _categoryId, value);
    }

    /// <summary>应用所属的多个分类。旧数据只有 CategoryId，加载时会自动迁移。</summary>
    public List<string> CategoryIds
    {
        get => _categoryIds;
        set => _categoryIds = value ?? new List<string>();
    }

    public bool IsInCategory(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return false;
        }

        var ids = _categoryIds;
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i].Equals(categoryId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public int LaunchCount { get; set; }

    public DateTimeOffset LastLaunchedAt { get; set; }

    [JsonIgnore]
    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetField(ref _iconSource, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
