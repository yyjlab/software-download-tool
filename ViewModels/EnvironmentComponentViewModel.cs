using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SoftwareDownloadCenter.Models;
using SoftwareDownloadCenter.Services;

namespace SoftwareDownloadCenter.ViewModels;

public sealed class EnvironmentComponentViewModel : INotifyPropertyChanged
{
    private static readonly Regex InstalledVersionPattern = new(
        @"(?<![\d.])v?(?<version>\d+(?:\.\d+){1,3}(?:_\d+)?(?:[-+][0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)(?!\d|\.\d)",
        RegexOptions.CultureInvariant);
    private bool _isSelected;
    private bool _isBusy;
    private double _progress;
    private string _statusText;
    private string _statusDetail = string.Empty;

    public EnvironmentComponentViewModel(EnvironmentComponent component, string installRoot)
    {
        Component = component;
        InstallRoot = installRoot;
        _isSelected = component.SelectedByDefault;
        _statusText = string.IsNullOrWhiteSpace(component.PackagePath)
            ? EnvironmentReleaseResolver.RequiresResolution(component)
                ? "待检测"
                : "未配置安装包"
            : "待检测";
    }

    public EnvironmentComponent Component { get; }
    public string InstallRoot { get; }
    public string Name => Component.Name;
    public string VersionText => Component.VersionPolicy.Trim().ToLowerInvariant() switch
    {
        "lateststable" when !HasResolvedVersion => "最新稳定版",
        "latestlts" when !HasResolvedVersion => "最新 LTS",
        _ => string.IsNullOrWhiteSpace(Component.Version) ? "版本待配置" : $"版本 {Component.Version}"
    };
    public string PackageText => string.IsNullOrWhiteSpace(Component.PackagePath)
        ? EnvironmentReleaseResolver.RequiresResolution(Component)
            ? "安装包：部署前从官方源解析"
            : "安装包：未配置"
        : $"安装包：{Path.GetFileName(Component.PackagePath)}";
    public string InstallDirectoryText => $"安装目录：{GetInstallDirectory()}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => SetField(ref _statusDetail, value);
    }

    public void SetInstalledStatus(string output)
    {
        StatusDetail = output;
        var versions = InstalledVersionPattern.Matches(output)
            .Select(match => match.Groups["version"].Value).ToArray();
        var version = versions.FirstOrDefault();
        if (Component.DeploymentMode.Equals("dotnetSdk", StringComparison.OrdinalIgnoreCase))
        {
            // SDK 可以并存；只提取每行 SDK 版本，不把目录名称中的数字当作版本。
            var sdkVersions = Regex.Matches(output, @"(?<version>\d+\.\d+\.\d+(?:[-+][\w.-]+)?)\s+\[")
                .Select(match => match.Groups["version"].Value).ToArray();
            version = sdkVersions.FirstOrDefault(value => value.Equals(Component.Version, StringComparison.OrdinalIgnoreCase))
                      ?? sdkVersions
                          .Select(value => (Value: value, Version: Version.TryParse(value.Split('-', '+')[0], out var parsed) ? parsed : null))
                          .Where(item => item.Version is not null)
                          .OrderByDescending(item => item.Version).FirstOrDefault().Value;
        }
        StatusText = string.IsNullOrEmpty(version) ? "已安装" : $"已安装 {version}";
    }

    public string GetInstallDirectory()
    {
        return EnvironmentDeploymentService.ResolveComponentInstallDirectory(InstallRoot, Component);
    }

    public void RefreshResolvedRelease()
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(PackageText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool HasResolvedVersion =>
        Version.TryParse(Component.Version.Trim().TrimStart('v'), out _);

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
