using Microsoft.Win32;
using SoftwareDownloadCenter.Models;
using SoftwareDownloadCenter.ViewModels;

namespace SoftwareDownloadCenter.Services;

public sealed class InstalledSoftwareService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledSoftware> GetInstalledSoftware()
    {
        var results = new Dictionary<string, InstalledSoftware>(StringComparer.OrdinalIgnoreCase);

        ReadRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry64, results);
        ReadRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry32, results);
        ReadRegistryHive(RegistryHive.CurrentUser, RegistryView.Registry64, results);
        ReadRegistryHive(RegistryHive.CurrentUser, RegistryView.Registry32, results);

        return results.Values.OrderBy(item => item.DisplayName).ToArray();
    }

    public static InstalledSoftware? FindMatch(
        SoftwarePackage package,
        IEnumerable<InstalledSoftware> installedSoftware)
    {
        var detectionName = string.IsNullOrWhiteSpace(package.DetectionDisplayNameContains)
            ? package.Name
            : package.DetectionDisplayNameContains;

        return installedSoftware
            .Where(item => item.DisplayName.Contains(detectionName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(
                item => item.Version,
                Comparer<string>.Create(VersionComparer.Compare))
            .FirstOrDefault();
    }

    private static void ReadRegistryHive(
        RegistryHive hive,
        RegistryView view,
        IDictionary<string, InstalledSoftware> results)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var version = subKey?.GetValue("DisplayVersion") as string ?? string.Empty;
                var key = $"{displayName}\u001f{version}";
                results.TryAdd(key, new InstalledSoftware(displayName, version));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 某些注册表分支可能不可读，不影响其余软件的检测。
        }
        catch (System.Security.SecurityException)
        {
            // 保持普通用户权限运行，跳过无权访问的分支。
        }
    }
}
