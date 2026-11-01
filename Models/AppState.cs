using System.Text.Json.Serialization;

namespace AppManager.Models;

/// <summary>用户配置，随应用数据一起保存在本地 JSON 中。</summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public HotkeyPreset Hotkey { get; set; } = HotkeyPreset.ControlR;

    public int WindowWidth { get; set; } = 1000;

    public int WindowHeight { get; set; } = 600;

    public string SelectedCategoryId { get; set; } = CategoryIds.All;
}

/// <summary>完整持久化状态。</summary>
public sealed class AppState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AppCategory> Categories { get; set; } = new();

    public List<AppItem> Apps { get; set; } = new();

    public AppSettings Settings { get; set; } = new();

    public static AppState CreateDefault()
    {
        return new AppState
        {
            Categories = CreateDefaultCategories(),
            Apps = new List<AppItem>(),
            Settings = new AppSettings()
        };
    }

    public static List<AppCategory> CreateDefaultCategories()
    {
        return new List<AppCategory>
        {
            new() { Id = CategoryIds.All, Name = "全部", IsAll = true, SortOrder = 0 },
            new() { Id = CategoryIds.Favorites, Name = "常用", SortOrder = 1 },
            new() { Id = CategoryIds.Tools, Name = "工具", SortOrder = 2 },
            new() { Id = CategoryIds.Games, Name = "游戏", SortOrder = 3 },
            new() { Id = CategoryIds.Other, Name = "其它", SortOrder = 4 }
        };
    }
}
