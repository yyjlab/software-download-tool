using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftwareDownloadCenter.Services;

// 只读取 EXE 的图标资源，不调用安装程序或文件关联的 Shell 扩展。
public sealed class InstallerIconService
{
    private readonly SemaphoreSlim _workers = new(4);
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftwareDownloadCenter", "IconCache");
    private static readonly Lazy<string?> InstallerTemplateHash = new(() =>
    {
        var template = Extract(Path.Combine(Environment.SystemDirectory, "msiexec.exe"));
        return template is null ? null : PixelHash(template);
    });

    public async Task<BitmapSource?> LoadAsync(string source)
    {
        string path;
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var resolved = SourceResolver.Resolve(source, AppContext.BaseDirectory);
            if (resolved.Kind != SourceKind.LocalFile
                || !string.Equals(Path.GetExtension(resolved.LocalPath), ".exe", StringComparison.OrdinalIgnoreCase))
                return null; // 不为远程 URL 下载整个安装包；MSI 的文件类型图标不是产品图标。
            path = resolved.LocalPath!;
        }
        catch (Exception exception) { Debug.WriteLine(exception.Message); return null; }

        var request = _requests.GetOrAdd(path, file => new Lazy<Task<BitmapSource?>>(
            () => Task.Run(() => LoadCoreAsync(file))));
        var task = request.Value;
        // 超时不终止系统资源读取；最多四个工作任务，句柄由工作任务自己释放。
        _ = task.ContinueWith(_ => _requests.TryRemove(
                new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(path, request)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private async Task<BitmapSource?> LoadCoreAsync(string path)
    {
        await _workers.WaitAsync();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var length = file.Length;
            var modified = file.LastWriteTimeUtc;
            var identity = $"{file.FullName.ToUpperInvariant()}|{length}|{modified.Ticks}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var cachePath = Path.Combine(_cacheDirectory, $"installer-v1-{key}.png");
            try
            {
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 1024 * 1024)
                {
                    using var stream = File.OpenRead(cachePath);
                    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count == 1 && decoder.Frames[0].PixelWidth <= 96 && decoder.Frames[0].PixelHeight <= 96)
                    {
                        var frame = decoder.Frames[0];
                        var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
                        var pixels = new byte[stride * frame.PixelHeight];
                        frame.CopyPixels(pixels, stride, 0);
                        var cached = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96,
                            frame.Format, frame.Palette, pixels, stride);
                        cached.Freeze();
                        return cached;
                    }
                }
            }
            catch (Exception exception) { Debug.WriteLine($"安装包图标缓存不可用: {exception.Message}"); }

            var image = Extract(path);
            if (image is null || PixelHash(image) == InstallerTemplateHash.Value) return null;
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
                return null; // 安装包读取过程中被替换时不缓存旧图标。
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = new MemoryStream();
                encoder.Save(output);
                var temporary = Path.Combine(_cacheDirectory, $"installer-v1-{key}.{Guid.NewGuid():N}.part");
                try
                {
                    await File.WriteAllBytesAsync(temporary, output.ToArray());
                    File.Move(temporary, cachePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception exception) { Debug.WriteLine($"安装包图标缓存写入失败: {exception.Message}"); }
            return image;
        }
        catch (Exception exception) { Debug.WriteLine($"安装包图标提取失败: {exception.Message}"); return null; }
        finally { _workers.Release(); }
    }

    private static BitmapSource? Extract(string path)
    {
        nint large = 0, small = 0;
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out large, out small, 96) != 0 || large == 0)
            {
                // 某些共享目录会被 SHDefExtractIcon 判断为慢链接，回退到直接资源提取。
                if (large != 0) DestroyIcon(large);
                if (small != 0 && small != large) DestroyIcon(small);
                large = small = 0;
                var count = ExtractIconEx(path, 0, out large, out small, 1);
                if (count == 0 || count == uint.MaxValue || large == 0) return null;
            }
            var image = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0 && small != large) DestroyIcon(small);
        }
    }

    private static string PixelHash(BitmapSource image)
    {
        var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = normalized.PixelWidth * 4;
        var pixels = new byte[stride * normalized.PixelHeight];
        normalized.CopyPixels(pixels, stride, 0);
        return $"{normalized.PixelWidth}x{normalized.PixelHeight}:" + Convert.ToHexString(SHA256.HashData(pixels));
    }

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIcon(string file, int index, uint flags,
        out nint large, out nint small, uint size);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
