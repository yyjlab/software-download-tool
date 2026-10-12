using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class SoftwareDownloadService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<string> DownloadAsync(
        SoftwarePackage package,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var source = SourceResolver.Resolve(package.DownloadUrl, AppContext.BaseDirectory);

        // 同名安装包、不同来源和不同版本不能共用半成品或最终文件。
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', package.Id, package.Version, package.DownloadUrl, package.Sha256, package.Sha512))));
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftwareDownloadCenter",
            "Cache", cacheKey);
        Directory.CreateDirectory(cacheDirectory);

        var fileName = GetSafeFileName(package, source);
        var finalPath = Path.Combine(cacheDirectory, fileName);
        var temporaryPath = finalPath + ".download";
        var cacheLock = CacheLocks.GetOrAdd(finalPath, _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            return await DownloadToCacheAsync(package, source, finalPath, temporaryPath, progress, cancellationToken);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<string> DownloadToCacheAsync(
        SoftwarePackage package, ResolvedSource source, string finalPath, string temporaryPath,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;

        // 没有可信摘要就无法判断旧半成品是否仍属于当前源文件，必须从头下载。
        if (string.IsNullOrWhiteSpace(package.Sha256) && string.IsNullOrWhiteSpace(package.Sha512))
        {
            existingLength = 0;
        }

        if (source.Kind == SourceKind.Http)
        {
            await DownloadFromHttpAsync(
                source.HttpUri!,
                temporaryPath,
                existingLength,
                progress,
                cancellationToken);
        }
        else
        {
            // 本地或共享目录中的源文件可能在两次运行之间被替换，不能把旧的半成品
            // 与新文件续接在一起。网络共享复制失败后从头重试，保证文件内容一致。
            await CopyFromLocalSourceAsync(
                source.LocalPath!,
                temporaryPath,
                0,
                progress,
                cancellationToken);
        }

        try
        {
            await VerifyHashAsync(temporaryPath, package.Sha256, cancellationToken, package.Sha512);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }

        File.Move(temporaryPath, finalPath, overwrite: true);
        progress.Report(100);
        return finalPath;
    }

    private async Task DownloadFromHttpAsync(
        Uri downloadUri,
        string temporaryPath,
        long existingLength,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            if (existingLength > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (existingLength > 0
                && (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                    || response.StatusCode == HttpStatusCode.PartialContent
                    && response.Content.Headers.ContentRange?.From != existingLength))
            {
                existingLength = 0;
                continue;
            }

            response.EnsureSuccessStatusCode();

            var isPartialResponse = response.StatusCode == HttpStatusCode.PartialContent;
            var range = response.Content.Headers.ContentRange;
            if (isPartialResponse
                && (range?.From != existingLength || range.To is null || !range.HasLength
                    || range.To < range.From
                    || range.To != range.Length - 1
                    || response.Content.Headers.ContentLength is { } responseLength
                    && responseLength != range.To - range.From + 1))
            {
                existingLength = 0;
                continue;
            }
            if (existingLength > 0 && !isPartialResponse)
            {
                existingLength = 0;
            }

            var contentLength = response.Content.Headers.ContentLength;
            var totalLength = isPartialResponse
                ? range!.Length
                : contentLength.HasValue ? contentLength.Value + existingLength : (long?)null;
            var fileMode = existingLength > 0 && isPartialResponse ? FileMode.Append : FileMode.Create;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             fileMode,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                var downloadedLength = existingLength;
                int readLength;

                while ((readLength = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, readLength), cancellationToken);
                    downloadedLength += readLength;
                    if (totalLength is > 0)
                    {
                        progress.Report(downloadedLength * 100d / totalLength.Value);
                    }
                }

                if (totalLength.HasValue && downloadedLength != totalLength.Value)
                {
                    throw new InvalidDataException("安装包下载不完整，已停止安装。请重试。");
                }
            }

            return;
        }

        throw new InvalidDataException("服务器返回了无效的断点续传响应，已停止下载。");
    }

    private static async Task CopyFromLocalSourceAsync(
        string sourcePath,
        string temporaryPath,
        long existingLength,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("没有找到本地或共享目录中的安装包。", sourcePath);
        }

        var sourceLength = new FileInfo(sourcePath).Length;
        if (existingLength > sourceLength)
        {
            existingLength = 0;
        }

        var fileMode = existingLength > 0 ? FileMode.Append : FileMode.Create;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            temporaryPath,
            fileMode,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        if (existingLength > 0)
        {
            source.Seek(existingLength, SeekOrigin.Begin);
        }

        var buffer = new byte[81920];
        var copiedLength = existingLength;
        int readLength;

        while ((readLength = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, readLength), cancellationToken);
            copiedLength += readLength;
            progress.Report(sourceLength == 0 ? 100 : copiedLength * 100d / sourceLength);
        }
    }

    private static string GetSafeFileName(SoftwarePackage package, ResolvedSource source)
    {
        var sourceFileName = source.Kind == SourceKind.Http
            ? Path.GetFileName(source.HttpUri!.LocalPath)
            : Path.GetFileName(source.LocalPath!);
        var fileName = string.IsNullOrWhiteSpace(package.FileName) ? sourceFileName : package.FileName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{package.Id}.exe";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidCharacter, '_');
        }

        return fileName;
    }

    private static async Task VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken,
        string expectedSha512 = "")
    {
        if (string.IsNullOrWhiteSpace(expectedHash) && string.IsNullOrWhiteSpace(expectedSha512))
        {
            return;
        }

        await using var stream = File.OpenRead(filePath);
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            var normalizedExpectedHash = expectedHash.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (!actualHash.Equals(normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("下载文件的 SHA-256 校验失败，安装已停止。");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha512))
        {
            stream.Position = 0;
            var actualHash = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken));
            var normalizedExpectedHash = expectedSha512.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (!actualHash.Equals(normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("下载文件的 SHA-512 校验失败，安装已停止。");
            }
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // 保留原始校验异常；临时文件会在下次完整下载时覆盖。
        }
        catch (UnauthorizedAccessException)
        {
            // 保留原始校验异常；临时文件会在下次完整下载时覆盖。
        }
    }
}
