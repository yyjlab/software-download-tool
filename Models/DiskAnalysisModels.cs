namespace SoftwareDownloadCenter.Models;

public sealed record DiskAnalysisProgress(
    string CurrentPath,
    long ScannedBytes,
    long FileCount,
    long DirectoryCount,
    long SkippedCount);

public sealed record DiskLargeFileInfo(
    string FullPath,
    long SizeBytes,
    DateTime LastWriteTime,
    bool CanRecycle,
    string TypeLabel);

public sealed record DiskDirectoryUsageInfo(string FullPath, long SizeBytes);

public sealed record DiskCleanupSuggestionInfo(
    string Name,
    string Description,
    long SizeBytes,
    long FileCount)
{
    public string RuleEvidence { get; init; } = string.Empty;
    public bool RequiresConfirmation { get; init; }
    public IReadOnlyList<string> FolderPaths { get; init; } = [];
}

public sealed record DiskAnalysisResult(
    string RootPath,
    long ScannedBytes,
    long FileCount,
    long DirectoryCount,
    long SkippedCount,
    long ProtectedLargeFileBytes,
    long ProtectedLargeFileCount,
    TimeSpan Duration,
    IReadOnlyList<DiskDirectoryUsageInfo> DirectoryUsages,
    IReadOnlyList<DiskCleanupSuggestionInfo> CleanupSuggestions,
    IReadOnlyList<DiskLargeFileInfo> LargeFiles);
