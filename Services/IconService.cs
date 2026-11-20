using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AppManager.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AppManager.Services;

/// <summary>
/// 将 exe / lnk / UWP 包资源转换为本地 PNG 图标缓存，避免每次启动重复读取系统图标。
/// </summary>
public sealed class IconService
{
    private readonly SemaphoreSlim _semaphore = new(4, 4);

    // 缓存源键 → 缓存路径，避免每次启动对每个应用重复计算 SHA256。
    private static readonly ConcurrentDictionary<string, string> CachePathCache = new(StringComparer.Ordinal);

    public IconService()
    {
        AppPaths.EnsureCreated();
    }

    public async Task LoadAllAsync(IEnumerable<AppItem> apps, CancellationToken cancellationToken = default)
    {
        var tasks = apps.Select(app => EnsureIconAsync(app, cancellationToken)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或主动取消时无需提示。
        }
        catch (Exception ex)
        {
            LogService.Error("批量加载应用图标失败。", ex);
        }
    }

    public async Task EnsureIconAsync(AppItem app, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // 缓存键包含源文件最后写入时间：应用更新换图标后会得到新键，
        // 不会永远命中第一次生成的旧 PNG。
        var cachePath = GetCachePath(app);
        if (File.Exists(cachePath))
        {
            app.IconPath = cachePath;
            AssignImageSource(app, cachePath);
            return;
        }

        // 源文件已变化时先顶上旧图标，避免列表短暂空白；下面重新提取后再替换。
        var fallbackPath = !string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath)
            ? app.IconPath
            : null;
        if (fallbackPath is not null)
        {
            AssignImageSource(app, fallbackPath);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cachePath))
            {
                app.IconPath = cachePath;
                AssignImageSource(app, cachePath);
                return;
            }

            var extracted = await ExtractToPngAsync(app, cachePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                app.IconPath = extracted;
                AssignImageSource(app, extracted);
                TryDeleteObsoleteCache(fallbackPath, extracted);
            }
            else if (fallbackPath is null)
            {
                // 既没有可用缓存也提取失败时清空，避免指向已被删除的文件。
                app.IconPath = null;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消。
        }
        catch (Exception ex)
        {
            LogService.Error($"加载图标失败：{app.Name} ({app.LaunchTarget})", ex);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>源文件最后写入时间，用于让图标缓存随应用更新自动失效。</summary>
    private static string GetSourceStamp(AppItem app)
    {
        try
        {
            var hint = app.IconSourceHint;
            if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint))
            {
                return File.GetLastWriteTimeUtc(hint).Ticks.ToString();
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"读取图标源文件时间戳失败：{app.IconSourceHint}", ex);
        }

        return "0";
    }

    /// <summary>
    /// 删除该应用不再引用的旧缓存文件，避免图标目录随着应用更新无限增长。
    /// 只处理位于本应用缓存目录内的文件，不会误删系统或其他位置的图标。
    /// </summary>
    private static void TryDeleteObsoleteCache(string? obsoletePath, string keepPath)
    {
        if (string.IsNullOrWhiteSpace(obsoletePath) ||
            string.Equals(obsoletePath, keepPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(obsoletePath);
            if (directory is not null &&
                directory.Equals(AppPaths.IconsDirectory, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(obsoletePath))
            {
                File.Delete(obsoletePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"清理旧图标缓存失败：{obsoletePath}", ex);
        }
    }

    private static async Task<string?> ExtractToPngAsync(
        AppItem app,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var hint = app.IconSourceHint;
        if (string.IsNullOrWhiteSpace(hint) || !File.Exists(hint))
        {
            if (!string.Equals(app.Source, "UWP", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(app.LaunchTarget) &&
                File.Exists(app.LaunchTarget))
            {
                hint = app.LaunchTarget;
            }
            else
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (IsSupportedImage(hint))
            {
                var imageFile = await StorageFile.GetFileFromPathAsync(hint);
                using var imageStream = await imageFile.OpenReadAsync();
                await EncodeToPngAsync(imageStream, destinationPath);
                return destinationPath;
            }

            // exe 和 lnk 都通过 Shell 缩略图拿应用图标。
            var sourceFile = await StorageFile.GetFileFromPathAsync(hint);
            using var thumbnail = await sourceFile.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                128,
                ThumbnailOptions.ResizeThumbnail);

            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            await EncodeToPngAsync(thumbnail, destinationPath);
            return destinationPath;
        }
        catch (UnauthorizedAccessException)
        {
            // 部分开始菜单项位于 ACL 受限目录（例如安装程序写入 ProgramData 的工具组），
            // StorageFile 会直接拒绝访问。这类条目往往指向已卸载的程序，
            // 返回 null 让界面显示占位图标即可，不必每次启动都记录一条错误。
            LogService.Info($"图标源文件不可访问，已跳过：{hint}");
            return null;
        }
    }

    private static async Task EncodeToPngAsync(
        Windows.Storage.Streams.IRandomAccessStream source,
        string destinationPath)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var maxSide = (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var scale = maxSide > 128 ? 128 / maxSide : 1;
        var pixelWidth = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var pixelHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = pixelWidth,
            ScaledHeight = pixelHeight,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destinationPath)!);
        var outputFile = await folder.CreateFileAsync(
            Path.GetFileName(destinationPath),
            CreationCollisionOption.ReplaceExisting);

        using var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
        outputStream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();
    }

    private static void AssignImageSource(AppItem app, string iconPath)
    {
        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = 112,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = new Uri(iconPath, UriKind.Absolute)
            };
            app.IconSource = bitmap;
        }
        catch (Exception ex)
        {
            LogService.Error($"创建图标位图失败：{iconPath}", ex);
        }
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCachePath(AppItem app)
    {
        var key = $"{app.Source}|{app.DedupeKey ?? app.LaunchTarget}|{app.IconSourceHint}|{GetSourceStamp(app)}";
        return CachePathCache.GetOrAdd(key, static k =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(k));
            return Path.Combine(AppPaths.IconsDirectory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".png");
        });
    }
}
