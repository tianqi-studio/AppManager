namespace AppManager.Services;

/// <summary>本地数据目录。所有数据均保存在当前用户的 LocalAppData 中。</summary>
public static class AppPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiquidAppManager");

    public static string DataFile { get; } = Path.Combine(RootDirectory, "appdata.json");

    public static string IconsDirectory { get; } = Path.Combine(RootDirectory, "icons");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string LogFile { get; } = Path.Combine(LogsDirectory, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(IconsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
