using System.IO;
using System.Text.Json;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class EnvironmentProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public async Task<EnvironmentProfileDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = EmbeddedConfiguration.Open("environments.json");
        var document = await JsonSerializer.DeserializeAsync<EnvironmentProfileDocument>(
                           stream,
                           JsonOptions,
                           cancellationToken)
                       ?? new EnvironmentProfileDocument();

        document.InstallRoot = NormalizeInstallRoot(document.InstallRoot);
        document.Profiles = document.Profiles
            .Where(profile => profile.Enabled)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.Name))
            .ToList();
        return document;
    }

    private static string NormalizeInstallRoot(string installRoot)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(installRoot) ? @"D:\software" : installRoot.Trim();
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
        var expectedRoot = Path.GetFullPath(@"D:\software");
        if (!fullPath.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("环境部署根目录必须是 D:\\software。请修改 environments.json。 ");
        }

        return fullPath;
    }
}
