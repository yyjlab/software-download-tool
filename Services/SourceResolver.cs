using System.IO;

namespace SoftwareDownloadCenter.Services;

internal enum SourceKind
{
    Http,
    LocalFile
}

internal sealed record ResolvedSource(SourceKind Kind, Uri? HttpUri, string? LocalPath)
{
    public static ResolvedSource FromHttp(Uri uri) => new(SourceKind.Http, uri, null);
    public static ResolvedSource FromLocalFile(string path) => new(SourceKind.LocalFile, null, path);
}

internal static class SourceResolver
{
    public static bool IsRelative(string source)
    {
        source = Environment.ExpandEnvironmentVariables(source?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return !Uri.TryCreate(source, UriKind.Absolute, out _);
    }

    public static ResolvedSource Resolve(string source, string baseDirectory)
    {
        source = Environment.ExpandEnvironmentVariables(source?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("没有配置文件地址。");
        }

        // 优先识别 Windows 盘符路径和 UNC 路径，避免把 E:\xxx 误判成 URI scheme。
        if (Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ResolvedSource.FromLocalFile(Path.GetFullPath(source));
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return ResolvedSource.FromLocalFile(Path.GetFullPath(uri.LocalPath));
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return ResolvedSource.FromHttp(uri);
            }

            throw new NotSupportedException($"不支持地址协议：{uri.Scheme}");
        }

        return ResolvedSource.FromLocalFile(Path.GetFullPath(source, baseDirectory));
    }
}
