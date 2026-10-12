using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class CatalogService
{
    private const string OfficeCategory = "办公软件";
    private const string FrontendCategory = "前端开发";
    private const string BackendCategory = "后端开发";
    private const string OtherCategory = "其他";

    private static readonly HashSet<string> SupportedInstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".msi"
    };

    private static readonly HashSet<string> SelectableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        OfficeCategory,
        FrontendCategory,
        BackendCategory
    };

    private static readonly (string Category, string[] Keywords)[] AutomaticCategoryRules =
    [
        (OfficeCategory,
        [
            "办公沟通", "办公软件", "dingtalk", "dingding", "钉钉", "wechat", "微信",
            "企业微信", "腾讯qq", "腾讯 qq", "qq.exe", "tim", "飞书", "feishu", "lark",
            "腾讯会议", "voov", "wps", "microsoft office", "outlook", "foxmail", "teams",
            "zoom", "搜狗输入法", "baidunetdisk", "百度网盘", "claude"
        ]),
        (FrontendCategory,
        [
            "前端开发", "visual studio code", "vscode", "vs_code", "webstorm", "node.js",
            "nodejs", "node-v", "npm", "pnpm", "yarn", "vue", "react", "angular",
            "figma", "chrome", "firefox", "edge浏览器"
        ]),
        (BackendCategory,
        [
            "后端开发", "intellij", "idea64", "pycharm", "goland", "jdk", "java se",
            "python", "dotnet-sdk", ".net sdk", "visual studio", "maven", "gradle", "postman",
            "insomnia", "mysql", "postgresql", "redis", "mongodb", "navicat", "dbeaver"
        ])
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<IReadOnlyList<SoftwarePackage>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "catalog.json");
        await using var stream = EmbeddedConfiguration.Open("catalog.json");
        var localCatalog = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream, JsonOptions, cancellationToken)
                           ?? new CatalogDocument();

        var catalog = localCatalog;
        var catalogSource = ResolvedSource.FromLocalFile(catalogPath);
        if (!string.IsNullOrWhiteSpace(localCatalog.CatalogUrl))
        {
            var source = SourceResolver.Resolve(localCatalog.CatalogUrl, AppContext.BaseDirectory);
            catalogSource = source;
            if (source.Kind == SourceKind.Http)
            {
                using var response = await _httpClient.GetAsync(source.HttpUri!, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                catalog = await DeserializeAsync(remoteStream, cancellationToken) ?? localCatalog;
            }
            else
            {
                if (!File.Exists(source.LocalPath))
                {
                    throw new FileNotFoundException("没有找到 catalogUrl 指向的软件目录。", source.LocalPath);
                }

                await using var referencedStream = File.OpenRead(source.LocalPath);
                catalog = await DeserializeAsync(referencedStream, cancellationToken) ?? localCatalog;
            }
        }

        ResolveRelativePackageSources(catalog, catalogSource);
        MergeAutoScanDirectories(catalog, catalogSource);
        ApplyCategories(catalog);

        return catalog.Packages
            .Where(package => package.Enabled)
            .Where(package => !string.IsNullOrWhiteSpace(package.Id) && !string.IsNullOrWhiteSpace(package.Name))
            .OrderBy(package => package.Category)
            .ThenBy(package => package.Name)
            .ToArray();
    }

    private static Task<CatalogDocument?> DeserializeAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        JsonSerializer.DeserializeAsync<CatalogDocument>(stream, JsonOptions, cancellationToken).AsTask();

    private static void ResolveRelativePackageSources(
        CatalogDocument catalog,
        ResolvedSource catalogSource)
    {
        foreach (var package in catalog.Packages)
        {
            if (!SourceResolver.IsRelative(package.DownloadUrl))
            {
                continue;
            }

            var relativeSource = Environment.ExpandEnvironmentVariables(package.DownloadUrl.Trim());
            if (catalogSource.Kind == SourceKind.Http)
            {
                package.DownloadUrl = new Uri(
                    catalogSource.HttpUri!,
                    relativeSource.Replace('\\', '/')).AbsoluteUri;
            }
            else
            {
                var catalogDirectory = Path.GetDirectoryName(catalogSource.LocalPath!)
                                       ?? AppContext.BaseDirectory;
                package.DownloadUrl = Path.GetFullPath(relativeSource, catalogDirectory);
            }
        }
    }

    private static void MergeAutoScanDirectories(
        CatalogDocument catalog,
        ResolvedSource catalogSource)
    {
        foreach (var configuredDirectory in catalog.AutoScanDirectories
                     .Where(directory => !string.IsNullOrWhiteSpace(directory)))
        {
            var scanDirectory = ResolveScanDirectory(configuredDirectory, catalogSource);
            if (scanDirectory is null || !Directory.Exists(scanDirectory))
            {
                // 网络共享临时不可用时保留原清单，避免软件列表被误清空。
                continue;
            }

            FileInfo[] discoveredFiles;
            try
            {
                discoveredFiles = new DirectoryInfo(scanDirectory)
                    .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .Where(file => SupportedInstallerExtensions.Contains(file.Extension))
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var discoveredPaths = discoveredFiles
                .Select(file => Path.GetFullPath(file.FullName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discoveredSizes = discoveredFiles.ToDictionary(
                file => Path.GetFullPath(file.FullName),
                TryGetFileSize,
                StringComparer.OrdinalIgnoreCase);

            catalog.Packages.RemoveAll(package =>
                TryGetLocalPackagePath(package, out var packagePath)
                && IsDirectChildOf(packagePath, scanDirectory)
                && !discoveredPaths.Contains(packagePath));

            foreach (var package in catalog.Packages)
            {
                if (TryGetLocalPackagePath(package, out var packagePath)
                    && discoveredSizes.TryGetValue(packagePath, out var fileSize))
                {
                    package.FileSizeBytes = fileSize;
                }
            }

            var configuredPaths = catalog.Packages
                .Select(package => TryGetLocalPackagePath(package, out var packagePath) ? packagePath : null)
                .Where(path => path is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in discoveredFiles.Where(file => !configuredPaths.Contains(file.FullName)))
            {
                catalog.Packages.Add(CreateAutomaticPackage(file.FullName, TryGetFileSize(file)));
            }
        }
    }

    private static string? ResolveScanDirectory(string configuredDirectory, ResolvedSource catalogSource)
    {
        var expandedDirectory = Environment.ExpandEnvironmentVariables(configuredDirectory.Trim());
        if (Path.IsPathFullyQualified(expandedDirectory) || expandedDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Path.GetFullPath(expandedDirectory);
        }

        if (catalogSource.Kind != SourceKind.LocalFile)
        {
            return null;
        }

        var catalogDirectory = Path.GetDirectoryName(catalogSource.LocalPath!) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(expandedDirectory, catalogDirectory);
    }

    private static bool TryGetLocalPackagePath(SoftwarePackage package, out string packagePath)
    {
        packagePath = string.Empty;
        try
        {
            var source = SourceResolver.Resolve(package.DownloadUrl, AppContext.BaseDirectory);
            if (source.Kind != SourceKind.LocalFile || string.IsNullOrWhiteSpace(source.LocalPath))
            {
                return false;
            }

            packagePath = Path.GetFullPath(source.LocalPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectChildOf(string filePath, string directoryPath)
    {
        var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var normalizedDirectory = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
            fileDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static SoftwarePackage CreateAutomaticPackage(string filePath, long? fileSizeBytes)
    {
        var fileName = Path.GetFileName(filePath);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var name = baseName;
        var description = $"从共享目录自动发现的 {baseName}";

        var isDingTalk = fileName.Contains("dingtalk", StringComparison.OrdinalIgnoreCase)
                         || fileName.Contains("dingding", StringComparison.OrdinalIgnoreCase);
        if (isDingTalk)
        {
            name = "钉钉";
            description = "钉钉安装程序（共享目录自动发现）";
        }

        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToUpperInvariant())));
        return new SoftwarePackage
        {
            Id = $"auto-{pathHash[..12].ToLowerInvariant()}",
            Name = name,
            Description = description,
            Category = "自动扫描",
            Version = string.Empty,
            DownloadUrl = filePath,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            Sha256 = string.Empty,
            SilentInstallArguments = string.Empty,
            DetectionDisplayNameContains = isDingTalk ? "钉钉" : name,
            VersionDetectionMode = isDingTalk ? "presenceOnly" : "registry",
            Enabled = true
        };
    }

    private static long? TryGetFileSize(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ApplyCategories(CatalogDocument catalog)
    {
        foreach (var package in catalog.Packages)
        {
            var searchableText = string.Join(
                '\n',
                package.Name,
                package.FileName,
                package.Description,
                package.Category,
                Path.GetFileName(package.DownloadUrl));

            var manualCategory = catalog.CategoryOverrides
                .Where(rule => rule.Enabled)
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Match))
                .Where(rule => SelectableCategories.Contains(rule.Category.Trim()))
                .FirstOrDefault(rule => searchableText.Contains(rule.Match.Trim(), StringComparison.OrdinalIgnoreCase))
                ?.Category
                .Trim();

            if (!string.IsNullOrWhiteSpace(manualCategory))
            {
                package.Category = manualCategory;
                continue;
            }

            if (SelectableCategories.Contains(package.Category))
            {
                continue;
            }

            package.Category = AutomaticCategoryRules
                                   .FirstOrDefault(rule => rule.Keywords.Any(keyword =>
                                       searchableText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                                   .Category
                               ?? OtherCategory;
        }
    }
}
