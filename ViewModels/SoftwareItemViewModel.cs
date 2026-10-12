using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.ViewModels;

public sealed class SoftwareItemViewModel : INotifyPropertyChanged
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)v?(?<version>\d+(?:\.\d+){1,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string _installedVersion = string.Empty;
    private string _statusText = "未安装";
    private string _actionText = "安装";
    private bool _isBusy;
    private bool _isActionAvailable = true;
    private bool _isUpdateAvailable;
    private double _progress;
    private ImageSource? _iconSource;

    public SoftwareItemViewModel(SoftwarePackage package)
    {
        Package = package;
    }

    public SoftwarePackage Package { get; }
    public string Name => Package.Name;
    public string Description => Package.Description;
    public string Category => Package.Category;
    public string Version => Package.Version;
    public string DisplayVersion => ResolveDisplayVersion();
    public string DisplayName => CleanDisplayName(Name, DisplayVersion);
    public string VersionText => string.IsNullOrWhiteSpace(DisplayVersion) ? "未提供" : DisplayVersion;
    public string FileSizeText => FormatFileSize(Package.FileSizeBytes);

    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetField(ref _iconSource, value);
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        private set => SetField(ref _installedVersion, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string ActionText
    {
        get => _actionText;
        set => SetField(ref _actionText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsActionEnabled));
            }
        }
    }

    public bool IsActionEnabled => !IsBusy && _isActionAvailable && !string.IsNullOrWhiteSpace(Package.DownloadUrl);

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetField(ref _isUpdateAvailable, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public void ApplyInstalledVersion(string? installedVersion)
    {
        InstalledVersion = installedVersion ?? string.Empty;

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            SetActionAvailability(true);
            IsUpdateAvailable = false;
            StatusText = "未安装";
            ActionText = "安装";
            return;
        }

        if (string.Equals(Package.VersionDetectionMode, "presenceOnly", StringComparison.OrdinalIgnoreCase))
        {
            SetActionAvailability(false);
            IsUpdateAvailable = false;
            StatusText = "已安装";
            ActionText = "已安装";
            return;
        }

        SetActionAvailability(true);
        var hasCatalogVersion = !string.IsNullOrWhiteSpace(Package.Version);
        IsUpdateAvailable = hasCatalogVersion
                            && VersionComparer.Compare(Package.Version, installedVersion) > 0;
        StatusText = IsUpdateAvailable
            ? "可更新"
            : "已安装";
        ActionText = IsUpdateAvailable ? "更新" : "重新安装";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetActionAvailability(bool value)
    {
        if (_isActionAvailable == value)
        {
            return;
        }

        _isActionAvailable = value;
        OnPropertyChanged(nameof(IsActionEnabled));
    }

    private static string FormatFileSize(long? sizeBytes)
    {
        if (sizeBytes is null || sizeBytes < 0)
        {
            return "—";
        }

        const double kiloByte = 1024d;
        const double megaByte = kiloByte * 1024d;
        const double gigaByte = megaByte * 1024d;

        return sizeBytes.Value switch
        {
            >= (long)gigaByte => $"{sizeBytes.Value / gigaByte:0.##} GB",
            >= (long)megaByte => $"{sizeBytes.Value / megaByte:0.##} MB",
            >= (long)kiloByte => $"{sizeBytes.Value / kiloByte:0.##} KB",
            _ => $"{sizeBytes.Value} B"
        };
    }

    private string ResolveDisplayVersion()
    {
        if (!string.IsNullOrWhiteSpace(Version))
        {
            return Version.Trim();
        }

        var match = VersionPattern.Match(Name);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static string CleanDisplayName(string name, string version)
    {
        var result = name.Trim();
        if (!string.IsNullOrWhiteSpace(version))
        {
            result = Regex.Replace(
                result,
                $@"[\s_-]*v?{Regex.Escape(version)}\s*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var majorVersion = version.Split('.')[0];
            if (version.Contains('.', StringComparison.Ordinal))
            {
                result = Regex.Replace(
                    result,
                    $@"[\s_-]+{Regex.Escape(majorVersion)}\s*$",
                    string.Empty,
                    RegexOptions.CultureInvariant);
            }
        }

        result = Regex.Replace(
            result.Trim(' ', '_', '-'),
            @"[\s_-]*(?:setup|(?<!un)installer|安装程序)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = result.Trim(' ', '_', '-');
        return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class VersionComparer
{
    public static int Compare(string latest, string installed)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return string.IsNullOrWhiteSpace(installed) ? 0 : -1;
        }

        if (string.IsNullOrWhiteSpace(installed))
        {
            return 1;
        }

        var latestParts = ExtractNumberParts(latest);
        var installedParts = ExtractNumberParts(installed);

        if (latestParts.Count == 0 && installedParts.Count == 0)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(latest, installed);
        }

        if (latestParts.Count == 0)
        {
            return -1;
        }

        if (installedParts.Count == 0)
        {
            return 1;
        }

        var length = Math.Max(latestParts.Count, installedParts.Count);
        for (var index = 0; index < length; index++)
        {
            var latestPart = index < latestParts.Count ? latestParts[index] : 0;
            var installedPart = index < installedParts.Count ? installedParts[index] : 0;
            var result = latestPart.CompareTo(installedPart);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static List<int> ExtractNumberParts(string value)
    {
        var parts = new List<int>();
        var current = string.Empty;

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                current += character;
            }
            else if (current.Length > 0)
            {
                parts.Add(int.TryParse(current, out var number) ? number : 0);
                current = string.Empty;
            }
        }

        if (current.Length > 0)
        {
            parts.Add(int.TryParse(current, out var number) ? number : 0);
        }

        return parts;
    }
}
