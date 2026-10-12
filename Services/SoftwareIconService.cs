using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

// 图标是可选的展示资源：加载失败不改变安装状态，也不阻塞软件目录加载。
public sealed class SoftwareIconService
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumDimension = 1024;
    private static readonly Lazy<Dictionary<string, string>> IconNames = new(() =>
    {
        using var stream = typeof(SoftwareIconService).Assembly.GetManifestResourceStream(
            "SoftwareDownloadCenter.Assets.software-icon-names.json");
        var names = stream is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return new Dictionary<string, string>(names ?? [], StringComparer.OrdinalIgnoreCase);
    });
    private static readonly Lazy<Dictionary<string, string>> BundledIcons = new(() =>
    {
        using var stream = typeof(SoftwareIconService).Assembly.GetManifestResourceStream(
            "SoftwareDownloadCenter.Assets.software-icons.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    });
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly InstallerIconService _installerIcons = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _requests = new();
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftwareDownloadCenter", "IconCache");
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public async Task<BitmapSource?> LoadPackageAsync(SoftwarePackage package, string displayName)
    {
        var configured = await LoadAsync(package.IconUrl, displayName).ConfigureAwait(false);
        return configured ?? await _installerIcons.LoadAsync(package.DownloadUrl).ConfigureAwait(false);
    }

    public async Task<BitmapSource?> LoadAsync(string? iconUrl, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(iconUrl) && displayName is not null)
            IconNames.Value.TryGetValue(displayName.Trim(), out iconUrl);
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            return null;

        var request = _requests.GetOrAdd(uri.AbsoluteUri, url => new Lazy<Task<BitmapSource?>>(
            () => Task.Run(() => LoadCoreAsync(new Uri(url)))));
        var image = await request.Value.ConfigureAwait(false);
        // 失败不永久记忆，用户重新加载列表后可重试；同一请求仍合并执行。
        if (image is null)
            _requests.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(uri.AbsoluteUri, request));
        return image;
    }

    private static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && !uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);

    private async Task<BitmapSource?> LoadCoreAsync(Uri uri)
    {
        await _concurrency.WaitAsync();
        BitmapSource? cached = null;
        try
        {
            // 已核实的官方图标随 EXE 发布，首次使用也不依赖网站和网络。
            if (BundledIcons.Value.TryGetValue(uri.AbsoluteUri, out var bundled))
                return Decode(Convert.FromBase64String(bundled));

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(_cacheDirectory, key + ".png");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= MaximumBytes)
                {
                    cached = Decode(await File.ReadAllBytesAsync(path));
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-30))
                        return cached;
                }
            }
            catch (Exception exception) { Debug.WriteLine($"图标缓存不可用: {exception.Message}"); }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await DownloadAsync(uri, timeout.Token);
            var image = Decode(bytes);
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                // 缓存统一保存为 PNG，避免保存网页或直接使用远程地址解码。
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                var temporaryPath = Path.Combine(_cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, stream.ToArray());
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) { Debug.WriteLine($"图标缓存写入失败: {exception.Message}"); }
            return image;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"图标加载失败: {uri.Host}: {exception.Message}");
            return cached;
        }
        finally { _concurrency.Release(); }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!IsAllowed(uri)) throw new InvalidDataException("图标地址必须使用 HTTPS。");
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes)
                throw new InvalidDataException("图标超过大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + count > MaximumBytes)
                    throw new InvalidDataException("图标超过大小限制。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("图标重定向次数过多。");
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder is not PngBitmapDecoder and not IconBitmapDecoder and not JpegBitmapDecoder)
            throw new InvalidDataException("只支持 PNG、ICO、JPEG 图标。");
        if (decoder.Frames.Count > 32 || decoder.Frames.Any(frame =>
                frame.PixelWidth > MaximumDimension || frame.PixelHeight > MaximumDimension))
            throw new InvalidDataException("图标尺寸超过限制。");
        var image = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        // 直接验证实际像素可读；不将延迟解码或网络访问带到 UI 线程。
        var stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        var bitmap = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
            image.Format, image.Palette, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
