namespace AppManager.Services;

/// <summary>轻量日志，便于排查后台运行时出现的问题。</summary>
public static class LogService
{
    /// <summary>单个日志文件上限；超过后滚动为 app.log.old，避免长期运行无限增长。</summary>
    private const long MaxLogBytes = 4L * 1024 * 1024;

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
                RotateIfNeeded();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志自身失败不能影响主程序。
        }
    }

    /// <summary>日志过大时滚存为 app.log.old，只保留最近一份历史日志。</summary>
    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backupPath = AppPaths.LogFile + ".old";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(AppPaths.LogFile, backupPath);
        }
        catch
        {
            // 滚动失败时继续写入原文件即可。
        }
    }
}
