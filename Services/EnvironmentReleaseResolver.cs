using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class EnvironmentReleaseResolver
{
    private const string PythonReleaseApiUrl = "https://www.python.org/api/v2/downloads/release/";
    private const string PythonReleaseFileApiUrl = "https://www.python.org/api/v2/downloads/release_file/";
    private const string AdoptiumReleaseInfoUrl = "https://api.adoptium.net/v3/info/available_releases";
    private const string NodeReleaseIndexUrl = "https://nodejs.org/dist/index.json";
    private const string GoReleaseIndexUrl = "https://go.dev/dl/?mode=json";
    private const string NvmLatestReleaseUrl = "https://api.github.com/repos/nvm-windows/nvm/releases/latest";
    private readonly HttpClient _httpClient;

    public EnvironmentReleaseResolver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("EnterpriseSoftwareCenter", "1.0"));
        }
    }

    public static bool RequiresResolution(EnvironmentComponent component) =>
        !string.Equals(component.VersionPolicy, "fixed", StringComparison.OrdinalIgnoreCase);

    public async Task ResolveAsync(EnvironmentComponent component, CancellationToken cancellationToken)
    {
        if (!RequiresResolution(component))
        {
            return;
        }

        var release = component.ReleaseProvider.Trim().ToLowerInvariant() switch
        {
            "javaadoptium" => await ResolveLatestJavaAsync(cancellationToken),
            "pythonofficial" => await ResolveLatestPythonAsync(cancellationToken),
            "nvmwindows" => await ResolveLatestNvmAsync(cancellationToken),
            "nodeofficiallts" => await ResolveLatestNodeLtsAsync(cancellationToken),
            "goofficial" => await ResolveLatestGoAsync(cancellationToken),
            "mavenofficial" => await ResolveLatestMavenAsync(cancellationToken),
            "pnpmofficial" => await ResolveGitHubToolAsync("pnpm/pnpm", _ => "pnpm-win32-x64.zip", cancellationToken, listReleases: true),
            "uvofficial" => await ResolveGitHubToolAsync("astral-sh/uv", _ => "uv-x86_64-pc-windows-msvc.zip", cancellationToken),
            "powershellofficial" => await ResolveGitHubToolAsync("PowerShell/PowerShell", version => $"PowerShell-{version}-win-x64.zip", cancellationToken),
            "dotnetofficiallts" => await ResolveLatestDotNetSdkAsync(cancellationToken),
            _ => throw new InvalidDataException(
                $"{component.Name} 没有配置有效的 releaseProvider。")
        };

        // 自动解析的官方安装包必须有有效摘要，不能因 API 字段缺失而静默跳过校验。
        // Node.js 的下载由 NVM 负责，不经过这里的安装包下载流程。
        if (!component.ReleaseProvider.Equals("nodeOfficialLts", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(release.Sha512))
                RequireHash(release.Sha512, 128, $"{component.Name} SHA-512");
            else
                RequireHash(release.Sha256, 64, $"{component.Name} SHA-256");
        }

        component.Version = release.Version;
        component.PackagePath = release.PackageUrl;
        component.Sha256 = release.Sha256;
        component.Sha512 = release.Sha512;
        component.ArchiveRoot = release.ArchiveRoot;
        switch (component.ReleaseProvider.Trim().ToLowerInvariant())
        {
            case "mavenofficial":
                component.VerifyOutputContains = $"Apache Maven {release.Version} ";
                break;
            case "pnpmofficial":
                component.VerifyOutputContains = release.Version;
                component.VerifyOutputIsExact = true;
                break;
            case "uvofficial":
                component.VerifyOutputContains = $"uv {release.Version}";
                component.VerifyOutputIsPrefix = true;
                break;
            case "powershellofficial":
                component.VerifyOutputContains = $"PowerShell:{release.Version};";
                component.VerifyOutputIsExact = true;
                break;
            case "dotnetofficiallts":
                component.VerifyOutputContains = $"{release.Version} [";
                break;
        }
        if (component.ReleaseProvider.Equals("goOfficial", StringComparison.OrdinalIgnoreCase))
        {
            component.VerifyOutputContains = $"go version go{release.Version} windows/amd64";
        }
        else if (component.ReleaseProvider.Equals("javaAdoptium", StringComparison.OrdinalIgnoreCase)
            || component.ReleaseProvider.Equals("pythonOfficial", StringComparison.OrdinalIgnoreCase)
            || component.ReleaseProvider.Equals("nvmWindows", StringComparison.OrdinalIgnoreCase)
            || component.ReleaseProvider.Equals("nodeOfficialLts", StringComparison.OrdinalIgnoreCase))
        {
            component.VerifyOutputContains = release.Version;
        }
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestMavenAsync(CancellationToken cancellationToken)
    {
        const string metadataUrl = "https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/maven-metadata.xml";
        await using var stream = await _httpClient.GetStreamAsync(metadataUrl, cancellationToken);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });
        var metadata = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        // Maven 3 稳定发行线；不把 Maven 4 的候选版混入通用部署。
        var latest = metadata.Descendants("version")
            .Select(element => Version.TryParse(element.Value, out var version) ? version : null)
            .Where(version => version is { Major: 3, Build: >= 0 })
            .OrderByDescending(version => version).FirstOrDefault()
            ?? throw new InvalidDataException("Apache Maven 官方源没有返回可用的 Maven 3 稳定版。");
        var fileName = $"apache-maven-{latest}-bin.zip";
        var path = $"maven/maven-3/{latest}/binaries/{fileName}";
        var sha512 = (await _httpClient.GetStringAsync($"https://downloads.apache.org/{path}.sha512", cancellationToken))
            .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        RequireHash(sha512, 128, "Apache Maven SHA-512");
        return new ResolvedEnvironmentRelease(latest.ToString(), $"https://dlcdn.apache.org/{path}", string.Empty)
        {
            Sha512 = sha512,
            ArchiveRoot = $"apache-maven-{latest}"
        };
    }

    private async Task<ResolvedEnvironmentRelease> ResolveGitHubToolAsync(
        string repository, Func<string, string> assetName, CancellationToken cancellationToken, bool listReleases = false)
    {
        var apiUrl = $"https://api.github.com/repos/{repository}/releases" + (listReleases ? "?per_page=100" : "/latest");
        using var document = await ReadJsonAsync(apiUrl, cancellationToken);
        var releases = listReleases
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        var stable = releases
            .Where(item => item.ValueKind == JsonValueKind.Object
                           && item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.False
                           && item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.False)
            .Select(item => (Release: item, Version: Version.TryParse(ReadString(item, "tag_name").TrimStart('v'), out var version) ? version : null))
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version).FirstOrDefault();
        if (stable.Version is null || !stable.Release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{repository} 没有返回有效的稳定版发布信息。");
        }
        var name = assetName(stable.Version.ToString());
        var asset = assets.EnumerateArray().FirstOrDefault(item => HasStringValue(item, "name", name));
        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"{repository} 最新稳定版缺少 Windows x64 安装包：{name}");
        }
        var url = ReadString(asset, "browser_download_url");
        var expectedUrl = $"https://github.com/{repository}/releases/download/{ReadString(stable.Release, "tag_name")}/{name}";
        if (!url.Equals(expectedUrl, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{repository} 返回的下载地址不属于预期官方发布。");
        }
        var digest = ReadString(asset, "digest");
        var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
        RequireHash(sha256, 64, $"{repository} SHA-256");
        return new ResolvedEnvironmentRelease(stable.Version.ToString(), url, sha256);
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestDotNetSdkAsync(CancellationToken cancellationToken)
    {
        const string metadataBase = "https://builds.dotnet.microsoft.com/dotnet/release-metadata";
        using var index = await ReadJsonAsync($"{metadataBase}/releases-index.json", cancellationToken);
        var channel = index.RootElement.GetProperty("releases-index").EnumerateArray()
            .Where(item => HasStringValue(item, "release-type", "lts")
                           && (HasStringValue(item, "support-phase", "active") || HasStringValue(item, "support-phase", "maintenance")))
            .Select(item => (Release: item, Version: Version.TryParse(ReadString(item, "channel-version"), out var version) ? version : null))
            .Where(item => item.Version is not null).OrderByDescending(item => item.Version).FirstOrDefault();
        var sdkVersion = channel.Version is null ? string.Empty : ReadString(channel.Release, "latest-sdk");
        if (!Version.TryParse(sdkVersion, out _))
        {
            throw new InvalidDataException("微软官方源没有返回受支持的 .NET LTS SDK 稳定版本。");
        }
        using var releases = await ReadJsonAsync($"{metadataBase}/{channel.Version}/releases.json", cancellationToken);
        foreach (var release in releases.RootElement.GetProperty("releases").EnumerateArray())
        {
            var sdks = new List<JsonElement>();
            if (release.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object) sdks.Add(sdk);
            if (release.TryGetProperty("sdks", out var sdkList) && sdkList.ValueKind == JsonValueKind.Array) sdks.AddRange(sdkList.EnumerateArray());
            foreach (var candidate in sdks.Where(item => HasStringValue(item, "version", sdkVersion)))
            {
                foreach (var file in candidate.GetProperty("files").EnumerateArray())
                {
                    if (!HasStringValue(file, "rid", "win-x64")) continue;
                    var url = ReadString(file, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps
                        || !(uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase)
                             || uri.Host.Equals("download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase))
                        || !Path.GetFileName(uri.LocalPath).Equals($"dotnet-sdk-{sdkVersion}-win-x64.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var sha512 = ReadString(file, "hash");
                    RequireHash(sha512, 128, ".NET SDK SHA-512");
                    return new ResolvedEnvironmentRelease(sdkVersion, url, string.Empty) { Sha512 = sha512 };
                }
            }
        }
        throw new InvalidDataException("微软最新 LTS SDK 缺少 Windows x64 EXE 安装包。");
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static void RequireHash(string hash, int length, string source)
    {
        if (hash.Length != length || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{source} 校验值无效，已停止部署。");
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestJavaAsync(
        CancellationToken cancellationToken)
    {
        await using var infoStream = await _httpClient.GetStreamAsync(
            AdoptiumReleaseInfoUrl,
            cancellationToken);
        using var infoDocument = await JsonDocument.ParseAsync(
            infoStream,
            cancellationToken: cancellationToken);
        if (!infoDocument.RootElement.TryGetProperty("most_recent_feature_release", out var featureElement)
            || !featureElement.TryGetInt32(out var featureVersion)
            || featureVersion <= 0)
        {
            throw new InvalidDataException("Adoptium 官方 API 没有返回最新 Java 功能版本。");
        }

        var releaseUrl = $"https://api.github.com/repos/adoptium/temurin{featureVersion}-binaries/releases/latest";
        await using var releaseStream = await _httpClient.GetStreamAsync(releaseUrl, cancellationToken);
        using var releaseDocument = await JsonDocument.ParseAsync(
            releaseStream,
            cancellationToken: cancellationToken);
        var root = releaseDocument.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.Trim()
                  ?? throw new InvalidDataException("Adoptium 官方发布信息缺少版本号。");
        var version = tag.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase)
            ? tag[4..]
            : tag;
        version = version.Split('+', 2)[0];

        var expectedPrefix = $"OpenJDK{featureVersion}U-jdk_x64_windows_hotspot_";
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(item =>
            {
                var name = item.GetProperty("name").GetString() ?? string.Empty;
                return name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                       && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            });
        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Adoptium 官方发布中没有找到 Windows x64 MSI 安装程序。");
        }

        var url = asset.GetProperty("browser_download_url").GetString()
                  ?? throw new InvalidDataException("Adoptium 官方安装包地址为空。");
        var digest = asset.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString() ?? string.Empty
            : string.Empty;
        var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : string.Empty;
        return new ResolvedEnvironmentRelease(version, url, sha256);
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestPythonAsync(
        CancellationToken cancellationToken)
    {
        await using var releaseStream = await _httpClient.GetStreamAsync(
            PythonReleaseApiUrl,
            cancellationToken);
        using var releaseDocument = await JsonDocument.ParseAsync(
            releaseStream,
            cancellationToken: cancellationToken);
        var releases = new List<(Version Version, string ResourceUri)>();
        foreach (var item in releaseDocument.RootElement.EnumerateArray())
        {
            var published = item.TryGetProperty("is_published", out var publishedElement)
                            && publishedElement.ValueKind == JsonValueKind.True;
            var preRelease = item.TryGetProperty("pre_release", out var preReleaseElement)
                             && preReleaseElement.ValueKind == JsonValueKind.True;
            var name = item.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var versionText = name.StartsWith("Python ", StringComparison.OrdinalIgnoreCase)
                ? name[7..].Trim()
                : string.Empty;
            var resourceUri = item.TryGetProperty("resource_uri", out var resourceElement)
                ? resourceElement.GetString() ?? string.Empty
                : string.Empty;
            if (published
                && !preRelease
                && Version.TryParse(versionText, out var version)
                && version.Major == 3
                && !string.IsNullOrWhiteSpace(resourceUri))
            {
                releases.Add((version, resourceUri));
            }
        }

        var latest = releases
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
        if (latest.Version is null)
        {
            throw new InvalidDataException("Python 官方发布 API 没有返回可用的稳定版。");
        }

        var releaseId = latest.ResourceUri.TrimEnd('/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            throw new InvalidDataException("Python 官方发布信息缺少 release ID。");
        }

        await using var fileStream = await _httpClient.GetStreamAsync(
            $"{PythonReleaseFileApiUrl}?release={Uri.EscapeDataString(releaseId)}",
            cancellationToken);
        using var fileDocument = await JsonDocument.ParseAsync(
            fileStream,
            cancellationToken: cancellationToken);
        foreach (var item in fileDocument.RootElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (!string.Equals(name, "Windows installer (64-bit)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = item.GetProperty("url").GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                break;
            }

            var sha256 = item.TryGetProperty("sha256_sum", out var shaElement)
                ? shaElement.GetString() ?? string.Empty
                : string.Empty;
            return new ResolvedEnvironmentRelease(latest.Version.ToString(), url, sha256);
        }

        throw new InvalidDataException("Python 官方发布 API 没有找到 Windows 64 位安装程序。");
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestNvmAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(NvmLatestReleaseUrl, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var version = root.GetProperty("tag_name").GetString()?.Trim().TrimStart('v')
                      ?? throw new InvalidDataException("NVM 官方发布信息缺少版本号。");
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(item =>
                item.GetProperty("name").GetString()?.EndsWith(
                    "-amd64-setup.exe",
                    StringComparison.OrdinalIgnoreCase) == true);
        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("NVM 官方发布中没有找到 Windows x64 安装程序。");
        }

        var url = asset.GetProperty("browser_download_url").GetString()
                  ?? throw new InvalidDataException("NVM 官方安装包地址为空。");
        var digest = asset.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString() ?? string.Empty
            : string.Empty;
        var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : string.Empty;
        return new ResolvedEnvironmentRelease(version, url, sha256);
    }

    private async Task<ResolvedEnvironmentRelease> ResolveLatestGoAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(GoReleaseIndexUrl, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Go 官方发布 API 没有返回有效的版本列表。");
        }

        var releases = new List<(Version Version, string Tag, JsonElement Release)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("stable", out var stable)
                || stable.ValueKind != JsonValueKind.True
                || !item.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var tag = versionElement.GetString() ?? string.Empty;
            if (tag.StartsWith("go", StringComparison.Ordinal)
                && Version.TryParse(tag[2..], out var version)
                && version.Major > 0)
            {
                releases.Add((version, tag, item));
            }
        }

        var latest = releases.OrderByDescending(item => item.Version).FirstOrDefault();
        if (latest.Version is null
            || !latest.Release.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Go 官方发布 API 没有返回可用的稳定版安装包。");
        }

        var expectedFileName = $"{latest.Tag}.windows-amd64.msi";
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object
                || !HasStringValue(file, "os", "windows")
                || !HasStringValue(file, "arch", "amd64")
                || !HasStringValue(file, "kind", "installer")
                || !HasStringValue(file, "filename", expectedFileName)
                || !HasStringValue(file, "version", latest.Tag))
            {
                continue;
            }

            var sha256 = file.TryGetProperty("sha256", out var hashElement)
                         && hashElement.ValueKind == JsonValueKind.String
                ? hashElement.GetString() ?? string.Empty
                : string.Empty;
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Go 官方安装包缺少有效的 SHA-256，已停止部署。");
            }

            return new ResolvedEnvironmentRelease(
                latest.Tag[2..], $"https://go.dev/dl/{expectedFileName}", sha256);
        }

        throw new InvalidDataException("Go 最新稳定版中没有找到 Windows x64 MSI 安装程序。");
    }

    private static bool HasStringValue(JsonElement element, string propertyName, string expectedValue) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal);

    private async Task<ResolvedEnvironmentRelease> ResolveLatestNodeLtsAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(NodeReleaseIndexUrl, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("lts", out var lts)
                || lts.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                continue;
            }

            var version = item.GetProperty("version").GetString()?.Trim().TrimStart('v');
            if (!string.IsNullOrWhiteSpace(version))
            {
                return new ResolvedEnvironmentRelease(version, string.Empty, string.Empty);
            }
        }

        throw new InvalidDataException("Node.js 官网没有返回可用的 LTS 版本。");
    }
}

public sealed record ResolvedEnvironmentRelease(string Version, string PackageUrl, string Sha256)
{
    public string Sha512 { get; init; } = string.Empty;
    public string ArchiveRoot { get; init; } = string.Empty;
}
