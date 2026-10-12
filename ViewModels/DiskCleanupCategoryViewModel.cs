using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.ViewModels;

public sealed class DiskCleanupCategoryViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isBusy;
    private bool _isEnabled = true;
    private long _sizeBytes;
    private int _fileCount;
    private bool _hasScanResult;
    private string _statusText = "等待扫描";

    public DiskCleanupCategoryViewModel(DiskCleanupCategory category)
    {
        Category = category;
        _isSelected = category.SelectedByDefault;
    }

    public DiskCleanupCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string RuleEvidence => Category.RuleEvidence;
    public IReadOnlyList<string> FolderPaths => Category.Targets
        .Select(target => target.RootPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public string OpenFolderText => Category.IsRecycleBin
        ? "打开回收站"
        : FolderPaths.Count > 1 ? "打开文件夹 ▾" : "打开文件夹";
    public string FolderToolTip => Category.IsRecycleBin
        ? "打开系统回收站。窗口可能显示其他磁盘的文件，此项目仅清空系统盘回收站。"
        : string.Join(Environment.NewLine, FolderPaths);
    public bool RequiresConfirmation => !Category.SelectedByDefault;
    public string HandlingText => Category.IsRecycleBin
        ? "永久清空"
        : Category.SendFilesToRecycleBin ? "移入回收站" : "直接删除";

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

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        private set
        {
            if (SetField(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public int FileCount
    {
        get => _fileCount;
        private set
        {
            if (SetField(ref _fileCount, value))
            {
                OnPropertyChanged(nameof(FileCountText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string SizeText => _hasScanResult ? FormatSize(SizeBytes) : "—";
    public string FileCountText => !_hasScanResult
        ? "等待结果"
        : FileCount == 0 ? "暂无可清理内容" : $"{FileCount:N0} 个文件";

    public void ResetScanResult()
    {
        _hasScanResult = false;
        SizeBytes = 0;
        FileCount = 0;
        StatusText = "等待扫描";
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FileCountText));
    }

    public void ApplyScanResult(DiskCleanupScanResult result)
    {
        _hasScanResult = true;
        SizeBytes = result.SizeBytes;
        FileCount = result.FileCount;
        StatusText = result.SkippedCount > 0
            ? $"跳过 {result.SkippedCount:N0} 项（无权限、不可用或受保护）"
            : "扫描完成";
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FileCountText));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, bytes);
        var value = (double)size;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} {units[unitIndex]}" : $"{value:N2} {units[unitIndex]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
