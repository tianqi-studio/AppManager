using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppManager.Models;

namespace AppManager.Services;

/// <summary>负责读写 JSON 数据文件，并在数据损坏时保留备份。</summary>
public sealed class AppDataService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new ThemeModeJsonConverter(), new JsonStringEnumConverter() }
    };

    public (AppState State, bool IsNew) LoadOrCreate()
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(AppPaths.DataFile))
        {
            var defaultState = AppState.CreateDefault();
            Save(defaultState);
            return (defaultState, true);
        }

        try
        {
            var json = File.ReadAllText(AppPaths.DataFile);
            var state = JsonSerializer.Deserialize<AppState>(json, SerializerOptions);
            if (state is null)
            {
                throw new JsonException("数据文件为空。");
            }

            var needsMigration = state.SchemaVersion < AppState.CurrentSchemaVersion;
            Normalize(state);
            if (needsMigration)
            {
                Save(state);
            }

            return (state, false);
        }
        catch (Exception ex)
        {
            LogService.Error("读取本地数据失败，已创建备份并恢复默认分类。", ex);
            TryBackupCorruptedFile();
            EnsurePaths();

            var fallback = AppState.CreateDefault();
            Save(fallback);
            return (fallback, true);
        }
    }

    public bool Save(AppState state)
    {
        try
        {
            EnsurePaths();
            var json = JsonSerializer.Serialize(state, SerializerOptions);
            var tempPath = AppPaths.DataFile + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, AppPaths.DataFile, true);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("保存本地数据失败。", ex);
            return false;
        }
    }

    public static void Normalize(AppState state)
    {
        EnsurePaths(state);

        if (state.Categories.Count == 0)
        {
            state.Categories = AppState.CreateDefaultCategories();
        }

        if (state.Categories.All(c => !c.IsAll))
        {
            state.Categories.Insert(0, new AppCategory
            {
                Id = CategoryIds.All,
                Name = "全部",
                IsAll = true,
                SortOrder = 0
            });
        }

        // 保证至少存在一个可以放应用的真实分类。
        if (state.Categories.All(c => c.IsAll))
        {
            state.Categories.Add(new AppCategory
            {
                Id = CategoryIds.Other,
                Name = "其它",
                SortOrder = 1
            });
        }

        // 清理重复分类 ID，并修正排序。
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = state.Categories.Count - 1; i >= 0; i--)
        {
            var category = state.Categories[i];
            category.Name = category.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(category.Id) ||
                !seenIds.Add(category.Id) ||
                string.IsNullOrWhiteSpace(category.Name) ||
                (!category.IsAll && !seenNames.Add(category.Name)))
            {
                state.Categories.RemoveAt(i);
            }
        }

        if (state.Categories.Count == 0 || state.Categories.All(c => !c.IsAll))
        {
            state.Categories.Add(new AppCategory
            {
                Id = CategoryIds.Other,
                Name = "其它",
                SortOrder = 1
            });
        }

        var allCategory = state.Categories.First(c => c.IsAll);
        allCategory.Id = CategoryIds.All;
        allCategory.Name = "全部";

        state.Categories = state.Categories
            .OrderBy(c => c.IsAll ? 0 : 1)
            .ThenBy(c => c.SortOrder)
            .ToList();
        for (var i = 0; i < state.Categories.Count; i++)
        {
            state.Categories[i].SortOrder = i;
        }

        var assignableCategoryIds = state.Categories
            .Where(c => !c.IsAll)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackCategoryId = state.Categories.First(c => !c.IsAll).Id;
        foreach (var app in state.Apps)
        {
            app.Id = string.IsNullOrWhiteSpace(app.Id) ? Guid.NewGuid().ToString("N") : app.Id;
            app.Name = string.IsNullOrWhiteSpace(app.Name) ? "未命名应用" : app.Name.Trim();
            app.LaunchTarget = app.LaunchTarget?.Trim() ?? string.Empty;
            app.Source = string.IsNullOrWhiteSpace(app.Source) ? "Desktop" : app.Source;

            // 旧数据只有 categoryId；新数据读取 categoryIds。
            var normalizedIds = app.CategoryIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && assignableCategoryIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedIds.Count == 0 &&
                !string.IsNullOrWhiteSpace(app.CategoryId) &&
                assignableCategoryIds.Contains(app.CategoryId))
            {
                normalizedIds.Add(app.CategoryId);
            }

            if (normalizedIds.Count == 0)
            {
                normalizedIds.Add(fallbackCategoryId);
            }

            app.CategoryIds = normalizedIds;
            app.CategoryId = normalizedIds[0];
        }

        state.SchemaVersion = AppState.CurrentSchemaVersion;

        state.Settings ??= new AppSettings();
        if (state.Settings.WindowWidth is < 680 or > 1800)
        {
            state.Settings.WindowWidth = 1000;
        }

        if (state.Settings.WindowHeight is < 460 or > 1100)
        {
            state.Settings.WindowHeight = 600;
        }

        if (state.Categories.All(c => c.Id != state.Settings.SelectedCategoryId))
        {
            state.Settings.SelectedCategoryId = CategoryIds.All;
        }
    }

    private static void EnsurePaths(AppState state)
    {
        state.Categories ??= new List<AppCategory>();
        state.Apps ??= new List<AppItem>();
        state.Settings ??= new AppSettings();
    }

    private static void EnsurePaths()
    {
        AppPaths.EnsureCreated();
    }

    private static void TryBackupCorruptedFile()
    {
        try
        {
            if (!File.Exists(AppPaths.DataFile))
            {
                return;
            }

            var backupPath = Path.Combine(
                AppPaths.RootDirectory,
                $"appdata.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(AppPaths.DataFile, backupPath, true);
            LogService.Info($"损坏的数据已备份到：{backupPath}");
        }
        catch (Exception ex)
        {
            LogService.Error("备份损坏数据失败。", ex);
        }
    }
}
