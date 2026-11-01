namespace AppManager.Models;

/// <summary>应用分类。应用可同时属于多个真实分类；“全部”是虚拟视图。</summary>
public sealed class AppCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>“全部”分类由程序保留，不允许重命名或删除。</summary>
    public bool IsAll { get; set; }

    public int SortOrder { get; set; }
}

public static class CategoryIds
{
    public const string All = "all";
    public const string Favorites = "favorites";
    public const string Tools = "tools";
    public const string Games = "games";
    public const string Other = "other";
}
