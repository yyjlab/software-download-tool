namespace SoftwareDownloadCenter.Models;

public sealed class EnvironmentProfileDocument
{
    public string InstallRoot { get; set; } = @"D:\software";
    public List<EnvironmentProfile> Profiles { get; set; } = [];
}

public sealed class EnvironmentProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<EnvironmentComponent> Components { get; set; } = [];
}

public sealed class EnvironmentComponent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string VersionPolicy { get; set; } = "fixed";
    public string ReleaseProvider { get; set; } = string.Empty;
    public string DeploymentMode { get; set; } = "standard";
    public string PackagePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Sha512 { get; set; } = string.Empty;
    public string ArchiveRoot { get; set; } = string.Empty;
    public string InstallerType { get; set; } = "exe";
    public string SilentInstallArguments { get; set; } = string.Empty;
    public string RelativeInstallDirectory { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
    public List<string> PathEntries { get; set; } = [];
    public string VerifyExecutable { get; set; } = string.Empty;
    public string PathDetectionExecutable { get; set; } = string.Empty;
    public string VerifyArguments { get; set; } = string.Empty;
    public string VerifyOutputContains { get; set; } = string.Empty;
    public bool VerifyOutputIsExact { get; set; }
    public bool VerifyOutputIsPrefix { get; set; }
    public bool SelectedByDefault { get; set; }
    public bool Required { get; set; }
}

public sealed record EnvironmentDeploymentResult(
    bool Success,
    bool Skipped,
    bool RebootRequired,
    string Message,
    string VerificationOutput = "");
