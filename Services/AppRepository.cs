using System.Collections.ObjectModel;
using AppManager.Models;

namespace AppManager.Services;

/// <summary>
/// 应用数据的唯一入口。UI 通过这里修改分类和应用，数据会立即写入 JSON。
/// </summary>
public sealed class AppRepository
{
    private readonly AppDataService _dataService;
    private readonly AppState _state;

    public AppRepository(AppDataService dataService, AppState state, bool isNew)
    {
        _dataService = dataService;
        _state = state;
        IsNew = isNew;

        Categories = new ObservableCollection<AppCategory>(state.Categories);
        Apps = new ObservableCollection<AppItem>(state.Apps);
    }

    public ObservableCollection<AppCategory> Categories { get; }

    public ObservableCollection<AppItem> Apps { get; }

    public AppSettings Settings => _state.Settings;

    public bool IsNew { get; }

    /// <summary>分类集合或分类名称/顺序变化时递增，供界面判断分类栏是否需要重建。</summary>
    public int CategoryVersion { get; private set; }

    /// <summary>应用集合或分类成员关系变化时递增，供界面判断应用列表是否需要重建。</summary>
    public int AppsVersion { get; private set; }

    public event EventHandler? Changed;

    public static AppRepository Load()
    {
        var dataService = new AppDataService();
        var (state, isNew) = dataService.LoadOrCreate();
        return new AppRepository(dataService, state, isNew);
    }

    public AppCategory? FindCategory(string? id)
    {
        return Categories.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public string GetCategoryName(string? id)
    {
        return FindCategory(id)?.Name ?? "其它";
    }

    public int GetCategoryCount(string categoryId)
    {
        if (categoryId.Equals(CategoryIds.All, StringComparison.OrdinalIgnoreCase))
        {
            return Apps.Count;
        }

        return Apps.Count(a => a.IsInCategory(categoryId));
    }

    public IReadOnlyList<AppCategory> GetAssignableCategories()
    {
        return Categories.Where(c => !c.IsAll).OrderBy(c => c.SortOrder).ToList();
    }

    private AppCategory? FindAssignableCategory(string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return null;
        }

        return Categories.FirstOrDefault(c =>
            !c.IsAll && c.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
    }

    public AppCategory AddCategory(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("分类名称不能为空。", nameof(name));
        }

        if (Categories.Any(c => c.Name.Equals(trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException("已经存在同名分类。");
        }

        var category = new AppCategory
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = trimmed,
            SortOrder = Categories.Count == 0 ? 0 : Categories.Max(c => c.SortOrder) + 1
        };
        Categories.Add(category);
        CategoryVersion++;
        PersistAndNotify();
        return category;
    }

    public void RenameCategory(string id, string newName)
    {
        var category = FindCategory(id) ?? throw new InvalidOperationException("找不到该分类。");
        if (category.IsAll)
        {
            throw new InvalidOperationException("“全部”分类不能重命名。");
        }

        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("分类名称不能为空。", nameof(newName));
        }

        if (Categories.Any(c =>
                !c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                c.Name.Equals(trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException("已经存在同名分类。");
        }

        category.Name = trimmed;
        CategoryVersion++;
        PersistAndNotify();
    }

    /// <summary>删除分类，并从所有应用中移除该分类；没有其他分类的应用会转移到兜底分类。</summary>
    public AppCategory DeleteCategory(string id)
    {
        var category = FindCategory(id) ?? throw new InvalidOperationException("找不到该分类。");
        if (category.IsAll)
        {
            throw new InvalidOperationException("“全部”分类不能删除。");
        }

        var fallback = Categories.FirstOrDefault(c =>
                           !c.IsAll &&
                           !c.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase) &&
                           c.Name.Equals("其它", StringComparison.CurrentCultureIgnoreCase))
                       ?? Categories.FirstOrDefault(c =>
                           !c.IsAll &&
                           !c.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));

        // 如果这是最后一个可放置应用的分类，则自动创建一个“未分类”兜底分类。
        if (fallback is null)
        {
            fallback = new AppCategory
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "未分类",
                SortOrder = category.SortOrder
            };
            Categories.Add(fallback);
        }

        foreach (var app in Apps.Where(a => a.IsInCategory(category.Id)))
        {
            app.CategoryIds.RemoveAll(id =>
                id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));
            if (app.CategoryIds.Count == 0)
            {
                app.CategoryIds.Add(fallback.Id);
            }

            app.CategoryId = app.CategoryIds[0];
        }

        Categories.Remove(category);
        if (Settings.SelectedCategoryId.Equals(category.Id, StringComparison.OrdinalIgnoreCase))
        {
            Settings.SelectedCategoryId = CategoryIds.All;
        }

        CategoryVersion++;
        AppsVersion++;
        RenumberCategories();
        PersistAndNotify();
        return fallback;
    }

    /// <summary>添加扫描结果，按 DedupeKey / LaunchTarget 去重，并自动归类。</summary>
    public int AddApps(IEnumerable<AppItem> incoming, bool autoClassify = true)
    {
        var existingKeys = Apps
            .Select(a => !string.IsNullOrWhiteSpace(a.DedupeKey) ? a.DedupeKey : a.LaunchTarget)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var assignableCategoryIds = Categories
            .Where(c => !c.IsAll)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var app in incoming)
        {
            var key = !string.IsNullOrWhiteSpace(app.DedupeKey) ? app.DedupeKey : app.LaunchTarget;
            if (string.IsNullOrWhiteSpace(key) || !existingKeys.Add(key))
            {
                continue;
            }

            var validCategoryIds = app.CategoryIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && assignableCategoryIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validCategoryIds.Count == 0 &&
                !string.IsNullOrWhiteSpace(app.CategoryId) &&
                assignableCategoryIds.Contains(app.CategoryId))
            {
                validCategoryIds.Add(app.CategoryId);
            }

            if (validCategoryIds.Count == 0)
            {
                validCategoryIds.Add(autoClassify
                    ? AppClassifier.ChooseCategoryId(app, Categories)
                    : Categories.FirstOrDefault(c => !c.IsAll)?.Id ?? CategoryIds.Other);
            }

            app.CategoryIds = validCategoryIds;
            app.CategoryId = validCategoryIds[0];

            Apps.Add(app);
            added++;
        }

        if (added > 0)
        {
            AppsVersion++;
            PersistAndNotify();
        }

        return added;
    }

    public void RemoveApp(string appId)
    {
        var app = Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.Ordinal));
        if (app is null)
        {
            return;
        }

        Apps.Remove(app);
        AppsVersion++;
        PersistAndNotify();
    }

    /// <summary>设置应用是否属于某个分类；应用至少保留一个分类。</summary>
    public void SetAppCategoryMembership(string appId, string categoryId, bool isMember)
    {
        var app = Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.Ordinal));
        var category = FindAssignableCategory(categoryId);
        if (app is null || category is null)
        {
            return;
        }

        var currentlyMember = app.IsInCategory(category.Id);
        if (currentlyMember == isMember)
        {
            return;
        }

        if (isMember)
        {
            app.CategoryIds.Add(category.Id);
        }
        else
        {
            if (app.CategoryIds.Count <= 1)
            {
                return;
            }

            app.CategoryIds.RemoveAll(id =>
                id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));
        }

        app.CategoryId = app.CategoryIds[0];
        AppsVersion++;
        LogService.Info($"应用「{app.Name}」已{(isMember ? "加入" : "移出")}分类「{category.Name}」。");
        PersistAndNotify();
    }

    public void MarkLaunched(string appId)
    {
        var app = Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.Ordinal));
        if (app is null)
        {
            return;
        }

        app.LaunchCount++;
        app.LastLaunchedAt = DateTimeOffset.Now;
        PersistAndNotify();
    }

    public void SetSelectedCategory(string categoryId)
    {
        if (FindCategory(categoryId) is null)
        {
            return;
        }

        Settings.SelectedCategoryId = categoryId;
        CategoryVersion++;
        AppsVersion++;
        PersistQuietly();
    }

    public void SetTheme(ThemeMode theme)
    {
        Settings.Theme = theme;
        PersistAndNotify();
    }

    public void SetHotkey(HotkeyPreset preset)
    {
        Settings.Hotkey = preset;
        PersistAndNotify();
    }

    public void SetWindowSize(int width, int height)
    {
        Settings.WindowWidth = Math.Clamp(width, 680, 1800);
        Settings.WindowHeight = Math.Clamp(height, 460, 1100);
        PersistAndNotify();
    }

    /// <summary>保存但不触发界面整体刷新，适合更新图标缓存路径。</summary>
    public void PersistQuietly()
    {
        _state.Categories = Categories.ToList();
        _state.Apps = Apps.ToList();
        _state.Settings = Settings;
        _dataService.Save(_state);
    }

    private void PersistAndNotify()
    {
        RenumberCategories();
        PersistQuietly();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RenumberCategories()
    {
        var ordered = Categories
            .OrderBy(c => c.IsAll ? 0 : 1)
            .ThenBy(c => c.SortOrder)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }
    }
}
