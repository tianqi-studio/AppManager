using AppManager.Models;

namespace AppManager.Services;

/// <summary>根据应用名称和路径给出一个尽量合理的默认分类。</summary>
public static class AppClassifier
{
    private static readonly string[] GameKeywords =
    {
        "steam", "epic games", "epicgames", "battle.net", "blizzard", "riot", "minecraft",
        "xbox", "wegame", "ubisoft", "rockstar", "genshin", "原神", "崩坏", "星穹铁道",
        "鸣潮", "game", "游戏", "游戏盒", "gamebox", "faceit", "ea app", "ea desktop"
    };

    private static readonly string[] ToolKeywords =
    {
        "visual studio", "vs code", "vscode", "cursor", "qoder", "pycharm", "intellij",
        "webstorm", "rider", "terminal", "powershell", "command prompt", "cmd", "git",
        "github", "7-zip", "winrar", "bandizip", "everything", "emeditor", "notepad++",
        "sublime", "hxd", "cheat engine", "process explorer", "sysinternals", "putty",
        "winscp", "filezilla", "adobe", "photoshop", "illustrator", "premiere",
        "after effects", "lightroom", "blender", "obs", "docker", "postman", "fiddler",
        "node.js", "python", "mysql", "wsl", "sandboxie", "bitcomet", "download",
        "工具", "开发", "终端", "编辑器", "压缩", "截图", "录屏", "驱动", "下载"
    };

    private static readonly string[] FavoriteKeywords =
    {
        "chrome", "edge", "firefox", "browser", "浏览器", "wechat", "weixin", "微信",
        "qq", "teams", "outlook", "mail", "邮件", "word", "excel", "powerpoint", "office",
        "onenote", "wps", "notion", "obsidian", "spotify", "网易云音乐", "音乐", "bilibili",
        "哔哩", "explorer", "文件资源管理器", "settings", "设置", "store", "商店",
        "chat", "聊天", "豆包", "doubao", "deepseek", "opencode"
    };

    public static string ChooseCategoryId(AppItem app, IReadOnlyList<AppCategory> categories)
    {
        // 不提前 ToLowerInvariant，直接 OrdinalIgnoreCase 匹配，减少字符串分配。
        var searchable = $"{app.Name} {app.LaunchTarget} {app.IconSourceHint}";

        string preferredId;
        if (GameKeywords.Any(k => searchable.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            preferredId = CategoryIds.Games;
        }
        else if (ToolKeywords.Any(k => searchable.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            preferredId = CategoryIds.Tools;
        }
        else if (FavoriteKeywords.Any(k => searchable.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            preferredId = CategoryIds.Favorites;
        }
        else
        {
            preferredId = CategoryIds.Other;
        }

        var exact = categories.FirstOrDefault(c => c.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Id;
        }

        // 用户如果删除了默认分类，则按名称寻找替代项。
        var preferredName = preferredId switch
        {
            CategoryIds.Games => "游戏",
            CategoryIds.Tools => "工具",
            CategoryIds.Favorites => "常用",
            _ => "其它"
        };
        var byName = categories.FirstOrDefault(c => c.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
        var fallback = byName ?? categories.FirstOrDefault(c => !c.IsAll);
        return fallback?.Id ?? CategoryIds.Other;
    }
}
