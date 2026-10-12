using System.IO;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.ViewModels;

public sealed class DiskDirectoryUsageViewModel
{
    public DiskDirectoryUsageViewModel(DiskDirectoryUsageInfo usage, long largestSize)
    {
        FullPath = usage.FullPath;
        DisplayName = usage.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        SizeText = DiskCleanupCategoryViewModel.FormatSize(usage.SizeBytes);
        Percentage = largestSize <= 0 ? 0 : usage.SizeBytes * 100d / largestSize;
    }

    public string FullPath { get; }
    public string DisplayName { get; }
    public string SizeText { get; }
    public double Percentage { get; }
}

public sealed class DiskLargeFileViewModel
{
    public DiskLargeFileViewModel(DiskLargeFileInfo file)
    {
        File = file;
    }

    public DiskLargeFileInfo File { get; }
    public string Name => Path.GetFileName(File.FullPath);
    public string DirectoryName => Path.GetDirectoryName(File.FullPath) ?? File.FullPath;
    public string SizeText => DiskCleanupCategoryViewModel.FormatSize(File.SizeBytes);
    public string ModifiedText => File.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    public string TypeLabel => File.TypeLabel;
    public string DetailText => $"{TypeLabel} · {DirectoryName}";
    public bool CanRecycle => File.CanRecycle;
    public string RecycleActionText => CanRecycle ? "移入回收站" : "系统保护";
}

public sealed class DiskCleanupSuggestionViewModel
{
    public DiskCleanupSuggestionViewModel(DiskCleanupSuggestionInfo suggestion)
    {
        Name = suggestion.Name;
        Description = suggestion.Description;
        SizeText = DiskCleanupCategoryViewModel.FormatSize(suggestion.SizeBytes);
        FileCountText = $"{suggestion.FileCount:N0} 个文件";
        RuleEvidence = suggestion.RuleEvidence;
        RiskText = suggestion.RequiresConfirmation ? "需手动确认 · 排障期间保留" : "已知临时文件／可重建缓存";
        FolderPaths = suggestion.FolderPaths;
    }

    public string Name { get; }
    public string Description { get; }
    public string SizeText { get; }
    public string FileCountText { get; }
    public string RuleEvidence { get; }
    public string RiskText { get; }
    public IReadOnlyList<string> FolderPaths { get; }
    public string OpenFolderText => FolderPaths.Count > 1 ? "打开文件夹 ▾" : "打开文件夹";
}
