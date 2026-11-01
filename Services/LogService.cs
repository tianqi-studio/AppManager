namespace AppManager.Services;

/// <summary>轻量日志，便于排查后台运行时出现的问题。</summary>
public static class LogService
{
    private static readonly object Gate = new();

    static LogService()
    {
        try
        {
            AppPaths.EnsureCreated();
        }
        catch
        {
            // 目录创建失败时后续写入会再次尝试并自行兜底。
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志自身失败不能影响主程序。
        }
    }
}
