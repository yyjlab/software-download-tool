using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class DiskCleanupService
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private const uint ShrBNoConfirmation = 0x00000001;
    private const uint ShrBNoProgressUi = 0x00000002;
    private const uint ShrBNoSound = 0x00000004;

    public IReadOnlyList<DiskCleanupCategory> CreateCategories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var downloadsDirectory = GetDownloadsDirectory();

        return
        [
            new DiskCleanupCategory
            {
                Id = "user-temp",
                Name = "用户临时文件",
                Description = "用途：保存软件运行、安装和解压时产生的临时文件。\n清理说明：只处理最后修改超过 24 小时的文件；年龄不代表已无用途，安装或解压期间建议保留。删除后不能从回收站恢复。",
                Targets = [new DiskCleanupTarget(Path.GetTempPath(), MinimumAge: TimeSpan.FromDays(1))]
            },
            new DiskCleanupCategory
            {
                Id = "windows-temp",
                Name = "Windows 临时文件",
                Description = "用途：保存 Windows 和安装程序产生的临时数据。\n清理说明：只处理最后修改超过 7 天的文件；正在安装或更新系统时建议保留。权限不足时跳过，删除后不能从回收站恢复。",
                Targets = [new DiskCleanupTarget(Path.Combine(windowsDirectory, "Temp"), MinimumAge: TimeSpan.FromDays(7))]
            },
            new DiskCleanupCategory
            {
                Id = "thumbnail-cache",
                Name = "缩略图缓存",
                Description = "用途：缓存图片预览和文件图标，加快资源管理器显示。\n清理说明：仅删除 thumbcache_*.db 和 iconcache_*.db，其他文件保留；Windows 会按需重新生成缓存。",
                Targets =
                [
                    new DiskCleanupTarget(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", false),
                    new DiskCleanupTarget(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", false)
                ]
            },
            new DiskCleanupCategory
            {
                Id = "crash-reports",
                Name = "错误报告与崩溃转储",
                Description = "用途：保存应用崩溃时的内存转储和 Windows 错误报告，用于排查故障。\n清理说明：仅列出指定报告目录内最后修改超过 7 天的文件；默认不选。删除后会失去这些诊断资料，排障期间请保留。",
                SelectedByDefault = false,
                Targets =
                [
                    new DiskCleanupTarget(Path.Combine(localAppData, "CrashDumps"), "*.dmp", MinimumAge: TimeSpan.FromDays(7)),
                    new DiskCleanupTarget(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"), MinimumAge: TimeSpan.FromDays(7)),
                    new DiskCleanupTarget(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"), MinimumAge: TimeSpan.FromDays(7)),
                    new DiskCleanupTarget(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"), MinimumAge: TimeSpan.FromDays(7)),
                    new DiskCleanupTarget(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"), MinimumAge: TimeSpan.FromDays(7))
                ]
            },
            new DiskCleanupCategory
            {
                Id = "shader-cache",
                Name = "DirectX 着色器缓存",
                Description = "用途：保存显卡和游戏编译过的着色器，减少重复编译。\n清理说明：仅处理 D3DSCache 内最后修改超过 24 小时的缓存；建议关闭游戏后清理，首次启动可能因重新编译而变慢。",
                Targets = [new DiskCleanupTarget(Path.Combine(localAppData, "D3DSCache"), MinimumAge: TimeSpan.FromDays(1))]
            },
            new DiskCleanupCategory
            {
                Id = "downloads",
                Name = "下载目录（30 天以上）",
                Description = "用途：保存下载的安装包、压缩包、文档等个人文件。\n清理说明：只处理超过 30 天的文件，请先确认不再需要；清理时移入回收站，仍占用磁盘空间。",
                SelectedByDefault = false,
                SendFilesToRecycleBin = true,
                Targets = [new DiskCleanupTarget(downloadsDirectory, MinimumAge: TimeSpan.FromDays(30))]
            },
            new DiskCleanupCategory
            {
                Id = "recycle-bin",
                Name = "C 盘回收站",
                Description = "用途：暂存系统盘上已删除的文件，便于恢复。\n清理说明：确认这些文件不再需要后才清空；清空后不能从回收站恢复。",
                SelectedByDefault = false,
                IsRecycleBin = true
            }
        ];
    }

    public Task<DiskCleanupScanResult> ScanAsync(DiskCleanupCategory category, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(category, cancellationToken), cancellationToken);

    public Task<DiskCleanupResult> CleanAsync(DiskCleanupCategory category, CancellationToken cancellationToken) =>
        Task.Run(() => Clean(category, cancellationToken), cancellationToken);

    private static DiskCleanupScanResult Scan(DiskCleanupCategory category, CancellationToken cancellationToken)
    {
        if (category.IsRecycleBin)
        {
            return QueryRecycleBin();
        }

        long totalBytes = 0;
        var fileCount = 0;
        var skippedCount = 0;
        foreach (var target in category.Targets)
        {
            if (!IsSafeTarget(category, target.RootPath))
            {
                skippedCount++;
                continue;
            }

            foreach (var filePath in EnumerateTargetFiles(target, cancellationToken, () => skippedCount++))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(filePath);
                    if (!MatchesTargetFile(target, file.FullName, file.LastWriteTimeUtc, file.Attributes, DateTime.UtcNow))
                    {
                        continue;
                    }

                    totalBytes += file.Length;
                    fileCount++;
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    skippedCount++;
                }
            }
        }

        return new DiskCleanupScanResult(totalBytes, fileCount, skippedCount);
    }

    private static DiskCleanupResult Clean(DiskCleanupCategory category, CancellationToken cancellationToken)
    {
        if (category.IsRecycleBin)
        {
            var before = QueryRecycleBin();
            var driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var result = SHEmptyRecycleBin(IntPtr.Zero, driveRoot, ShrBNoConfirmation | ShrBNoProgressUi | ShrBNoSound);
            if (result != 0)
            {
                return new DiskCleanupResult(0, 0, Math.Max(1, before.FileCount));
            }

            return new DiskCleanupResult(before.SizeBytes, before.FileCount, 0);
        }

        long deletedBytes = 0;
        var deletedCount = 0;
        var skippedCount = 0;
        foreach (var target in category.Targets)
        {
            if (!IsSafeTarget(category, target.RootPath))
            {
                skippedCount++;
                continue;
            }

            foreach (var filePath in EnumerateTargetFiles(target, cancellationToken, () => skippedCount++))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(filePath);
                    if (!MatchesTargetFile(target, file.FullName, file.LastWriteTimeUtc, file.Attributes, DateTime.UtcNow)
                        || HasReparsePointInPath(file.FullName, target.RootPath))
                    {
                        continue;
                    }

                    var size = file.Length;
                    if (category.SendFilesToRecycleBin)
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            file.FullName,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                    }
                    else
                    {
                        file.Delete();
                    }
                    deletedBytes += size;
                    deletedCount++;
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    skippedCount++;
                }
            }
        }

        return new DiskCleanupResult(deletedBytes, deletedCount, skippedCount);
    }

    private static IEnumerable<string> SafeGetDirectories(string rootPath)
    {
        try
        {
            return Directory.Exists(rootPath) ? Directory.GetDirectories(rootPath) : [];
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateTargetFiles(
        DiskCleanupTarget target,
        CancellationToken cancellationToken,
        Action reportSkipped)
    {
        if (!Directory.Exists(target.RootPath))
        {
            yield break;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(target.RootPath);
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            string[] files;
            try
            {
                // 目录入栈后可能已被替换；访问前重新检查完整父目录链。
                if (HasReparsePointInPath(Path.Combine(currentDirectory, ".scan-check"), target.RootPath))
                {
                    reportSkipped();
                    continue;
                }
                files = Directory.GetFiles(currentDirectory, target.SearchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                reportSkipped();
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (!target.Recursive)
            {
                continue;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(currentDirectory);
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                reportSkipped();
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pendingDirectories.Push(directory);
                    }
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    reportSkipped();
                }
            }
        }
    }

    // 快速扫描、执行清理和深度分析使用同一套路径、文件类型和年龄规则。
    internal static bool MatchesTargetFile(
        DiskCleanupTarget target, string filePath, DateTime lastWriteTimeUtc,
        FileAttributes attributes, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(target.RootPath)
            || (attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.RootPath));
        if (!IsPathWithinOrEqual(fullPath, root) || fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (target.Recursive || string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase))
               && FileSystemName.MatchesSimpleExpression(target.SearchPattern, Path.GetFileName(fullPath), ignoreCase: true)
               && (target.MinimumAge is null || lastWriteTimeUtc <= nowUtc.Subtract(target.MinimumAge.Value));
    }

    internal static bool HasReparsePointInPath(string filePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!IsPathWithinOrEqual(Path.GetFullPath(filePath), root))
        {
            return true;
        }
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSafeTarget(DiskCleanupCategory category, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var driveRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty);
            var windowsRoot = Path.TrimEndingDirectorySeparator(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var userRoot = Path.TrimEndingDirectorySeparator(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (HasReparsePointInPath(Path.Combine(fullPath, ".target-check"), fullPath))
            {
                return false;
            }

            if (string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, windowsRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, userRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(category.Id, "downloads", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var protectedRoots = new[]
            {
                windowsRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };
            return protectedRoots.All(protectedRoot =>
                !IsPathWithinOrEqual(fullPath, protectedRoot));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithinOrEqual(string path, string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(
                   normalizedParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException;

    private static string GetDownloadsDirectory()
    {
        var folderId = DownloadsFolderId;
        var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPointer);
        if (result == 0 && pathPointer != IntPtr.Zero)
        {
            try
            {
                var path = Marshal.PtrToStringUni(pathPointer);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    private static DiskCleanupScanResult QueryRecycleBin()
    {
        var info = new ShQueryRbInfo
        {
            Size = Marshal.SizeOf<ShQueryRbInfo>()
        };
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var result = SHQueryRecycleBin(driveRoot, ref info);
        return result == 0
            ? new DiskCleanupScanResult(info.TotalSize, ClampToInt(info.ItemCount), 0)
            : new DiskCleanupScanResult(0, 0, 1);
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long TotalSize;
        public long ItemCount;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo queryInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr windowHandle, string rootPath, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        IntPtr accessToken,
        out IntPtr pathPointer);
}
