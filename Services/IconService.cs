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

        if (!string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath))
        {
            AssignImageSource(app, app.IconPath);
            return;
        }

        var cachePath = GetCachePath(app);
        if (File.Exists(cachePath))
        {
            app.IconPath = cachePath;
            AssignImageSource(app, cachePath);
            return;
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
        var key = $"{app.Source}|{app.DedupeKey ?? app.LaunchTarget}|{app.IconSourceHint}";
        return CachePathCache.GetOrAdd(key, static k =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(k));
            return Path.Combine(AppPaths.IconsDirectory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".png");
        });
    }
}
