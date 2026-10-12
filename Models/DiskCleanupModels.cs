namespace SoftwareDownloadCenter.Models;

public sealed record DiskCleanupTarget(
    string RootPath,
    string SearchPattern = "*",
    bool Recursive = true,
    TimeSpan? MinimumAge = null)
{
    public string RuleText => $"目录：{RootPath}\n匹配：{SearchPattern}（{(Recursive ? "包含子目录" : "仅此目录")}）\n" +
                              (MinimumAge is { } age
                                  ? $"时间：最后修改距今至少 {age.TotalDays:N0} 天（不是最后使用时间）"
                                  : "时间：不按文件年龄筛选") +
                              "\n保护：跳过系统属性文件和符号链接／目录联接";
}

public sealed class DiskCleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool SelectedByDefault { get; init; } = true;
    public bool IsRecycleBin { get; init; }
    public bool SendFilesToRecycleBin { get; init; }
    public IReadOnlyList<DiskCleanupTarget> Targets { get; init; } = [];
    public string RuleEvidence => $"规则：{Id}\n" + (IsRecycleBin
        ? "仅统计和清空系统盘回收站；清空后不能从回收站恢复。"
        : string.Join("\n\n", Targets.Select(target => target.RuleText)));
}

public sealed record DiskCleanupScanResult(long SizeBytes, int FileCount, int SkippedCount);

public sealed record DiskCleanupResult(long DeletedBytes, int DeletedCount, int SkippedCount);
