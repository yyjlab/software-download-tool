using System.Diagnostics;
using System.IO;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class DiskAnalysisService
{
    private const int MaximumLargeFileResults = 1000;

    public string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public Task<DiskAnalysisResult> ScanSystemDriveAsync(
        long minimumLargeFileSize,
        IProgress<DiskAnalysisProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => Scan(SystemDriveRoot, minimumLargeFileSize, progress, cancellationToken),
            cancellationToken);

    public Task MoveToRecycleBinAsync(DiskLargeFileInfo file, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.CanRecycle || !CanRecycleUserFile(file.FullPath))
            {
                throw new InvalidOperationException("系统目录和应用数据目录中的文件不允许通过深度分析页面删除。");
            }

            if (!File.Exists(file.FullPath))
            {
                throw new FileNotFoundException("文件已经不存在，请重新扫描。", file.FullPath);
            }

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                file.FullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }, cancellationToken);

    private static DiskAnalysisResult Scan(
        string rootPath,
        long minimumLargeFileSize,
        IProgress<DiskAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastProgressAt = TimeSpan.Zero;
        var directorySizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cleanupSuggestions = new Dictionary<string, SuggestionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var largeFiles = new PriorityQueue<DiskLargeFileInfo, long>();
        var pendingDirectories = new Stack<(string Path, string TopLevelPath)>();
        var normalizedRoot = Path.GetFullPath(rootPath);
        pendingDirectories.Push((normalizedRoot, normalizedRoot));
        var knownCleanupRules = new DiskCleanupService().CreateCategories()
            .Where(category => !category.IsRecycleBin && !category.SendFilesToRecycleBin)
            .ToArray();

        long scannedBytes = 0;
        long fileCount = 0;
        long directoryCount = 0;
        long skippedCount = 0;
        long protectedLargeFileBytes = 0;
        long protectedLargeFileCount = 0;
        var currentPath = normalizedRoot;

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            currentPath = current.Path;
            directoryCount++;

            string[] files;
            try
            {
                files = Directory.GetFiles(current.Path, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                skippedCount++;
                ReportProgressIfNeeded(force: false);
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(filePath);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedCount++;
                        continue;
                    }
                    var size = file.Length;
                    scannedBytes += size;
                    fileCount++;

                    var topLevelPath = string.Equals(current.Path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        ? normalizedRoot
                        : current.TopLevelPath;
                    directorySizes[topLevelPath] = directorySizes.GetValueOrDefault(topLevelPath) + size;

                    var suggestion = ClassifyCleanupSuggestion(file, knownCleanupRules);
                    if (suggestion is not null)
                    {
                        if (!cleanupSuggestions.TryGetValue(suggestion.Id, out var accumulator))
                        {
                            accumulator = new SuggestionAccumulator(suggestion);
                            cleanupSuggestions.Add(suggestion.Id, accumulator);
                        }

                        accumulator.SizeBytes += size;
                        accumulator.FileCount++;
                    }

                    if (size >= minimumLargeFileSize)
                    {
                        var canRecycle = CanRecycleUserFile(file.FullName);
                        if (canRecycle)
                        {
                            var candidate = new DiskLargeFileInfo(
                                file.FullName,
                                size,
                                file.LastWriteTime,
                                true,
                                ClassifyUserFileType(file));
                            largeFiles.Enqueue(candidate, size);
                            if (largeFiles.Count > MaximumLargeFileResults)
                            {
                                largeFiles.Dequeue();
                            }
                        }
                        else
                        {
                            protectedLargeFileBytes += size;
                            protectedLargeFileCount++;
                        }
                    }
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    skippedCount++;
                }

                ReportProgressIfNeeded(force: false);
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current.Path);
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                skippedCount++;
                ReportProgressIfNeeded(force: false);
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedCount++;
                        continue;
                    }

                    var topLevelPath = string.Equals(current.Path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        ? directory
                        : current.TopLevelPath;
                    pendingDirectories.Push((directory, topLevelPath));
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    skippedCount++;
                }
            }

            ReportProgressIfNeeded(force: false);
        }

        ReportProgressIfNeeded(force: true);
        stopwatch.Stop();

        var directoryResults = directorySizes
            .Select(item => new DiskDirectoryUsageInfo(item.Key, item.Value))
            .OrderByDescending(item => item.SizeBytes)
            .ToArray();
        var largeFileResults = largeFiles.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.SizeBytes)
            .ToArray();
        var suggestionResults = cleanupSuggestions.Values
            .Where(item => item.SizeBytes > 0)
            .Select(item => new DiskCleanupSuggestionInfo(
                item.Category.Name,
                item.Category.Description,
                item.SizeBytes,
                item.FileCount)
            {
                RuleEvidence = item.Category.RuleEvidence,
                RequiresConfirmation = !item.Category.SelectedByDefault,
                FolderPaths = item.Category.Targets.Select(target => target.RootPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .OrderBy(item => item.RequiresConfirmation)
            .ThenByDescending(item => item.SizeBytes)
            .ToArray();

        return new DiskAnalysisResult(
            normalizedRoot,
            scannedBytes,
            fileCount,
            directoryCount,
            skippedCount,
            protectedLargeFileBytes,
            protectedLargeFileCount,
            stopwatch.Elapsed,
            directoryResults,
            suggestionResults,
            largeFileResults);

        void ReportProgressIfNeeded(bool force)
        {
            if (!force && stopwatch.Elapsed - lastProgressAt < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            lastProgressAt = stopwatch.Elapsed;
            progress?.Report(new DiskAnalysisProgress(
                currentPath,
                scannedBytes,
                fileCount,
                directoryCount,
                skippedCount));
        }
    }

    private static DiskCleanupCategory? ClassifyCleanupSuggestion(
        FileInfo file, IReadOnlyList<DiskCleanupCategory> knownRules)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var category in knownRules)
        {
            if (category.Targets.Any(target => DiskCleanupService.MatchesTargetFile(
                    target, file.FullName, file.LastWriteTimeUtc, file.Attributes, nowUtc)))
            {
                return category;
            }
        }
        // 未命中已知规则的文件只参与占用统计，绝不凭大小、扩展名或 Cache 目录名推断为垃圾。
        return null;
    }

    private static string ClassifyUserFileType(FileInfo file)
    {
        return file.Extension.ToLowerInvariant() switch
        {
            ".exe" or ".msi" or ".msix" or ".appx" => "安装包",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "压缩包",
            ".iso" or ".img" => "磁盘镜像",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "视频文件",
            ".bak" or ".backup" => "备份文件",
            _ => "个人大文件"
        };
    }

    private static bool CanRecycleUserFile(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appDataRoot = Path.Combine(userProfile, "AppData");
            if (!IsPathWithin(fullPath, userProfile)
                || IsPathWithin(fullPath, appDataRoot))
            {
                return false;
            }

            return (File.GetAttributes(fullPath) & (FileAttributes.System | FileAttributes.ReparsePoint)) == 0
                   && !DiskCleanupService.HasReparsePointInPath(fullPath, userProfile);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithin(string path, string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        return normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException or PathTooLongException;

    private sealed class SuggestionAccumulator(DiskCleanupCategory category)
    {
        public DiskCleanupCategory Category { get; } = category;
        public long SizeBytes { get; set; }
        public long FileCount { get; set; }
    }
}
