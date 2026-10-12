namespace SoftwareDownloadCenter.Models;

public sealed class CatalogDocument
{
    public string CatalogUrl { get; set; } = string.Empty;
    public List<string> AutoScanDirectories { get; set; } = [];
    public List<CategoryOverrideRule> CategoryOverrides { get; set; } = [];
    public List<SoftwarePackage> Packages { get; set; } = [];
}

public sealed class CategoryOverrideRule
{
    public string Match { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class SoftwarePackage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string Category { get; set; } = "全部";
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long? FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Sha512 { get; set; } = string.Empty;
    public string SilentInstallArguments { get; set; } = string.Empty;
    public string DetectionDisplayNameContains { get; set; } = string.Empty;
    public string VersionDetectionMode { get; set; } = "registry";
    public bool Enabled { get; set; } = true;
}

public sealed record InstalledSoftware(string DisplayName, string Version);
