using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SoftwareDownloadCenter.Services;
using SoftwareDownloadCenter.ViewModels;

namespace SoftwareDownloadCenter.Views;

public partial class DiskCleanupView : UserControl
{
    private readonly DiskCleanupService _cleanupService = new();
    private static string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private CancellationTokenSource? _scanCancellation;
    private bool _hasScanned;
    private bool _isScanning;
    private bool _isCleaning;

    public DiskCleanupView()
    {
        InitializeComponent();
        Categories = new ObservableCollection<DiskCleanupCategoryViewModel>(
            _cleanupService.CreateCategories().Select(category => new DiskCleanupCategoryViewModel(category)));
        DataContext = this;
        Loaded += DiskCleanupView_Loaded;
        IsVisibleChanged += DiskCleanupView_IsVisibleChanged;
        Unloaded += DiskCleanupView_Unloaded;
    }

    public ObservableCollection<DiskCleanupCategoryViewModel> Categories { get; }

    private async void DiskCleanupView_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshDiskSpace();
        if (IsVisible && !_hasScanned)
        {
            await ScanAllAsync();
        }
    }

    private async void DiskCleanupView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
        {
            RefreshDiskSpace();
        }

        if (IsLoaded && IsVisible && !_hasScanned)
        {
            await ScanAllAsync();
        }
    }

    private void DiskCleanupView_Unloaded(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning)
        {
            _scanCancellation?.Cancel();
            return;
        }

        if (!_isCleaning)
        {
            await ScanAllAsync();
        }
    }

    private async Task ScanAllAsync()
    {
        if (_isScanning || _isCleaning)
        {
            return;
        }

        _isScanning = true;
        _hasScanned = false;
        _scanCancellation = new CancellationTokenSource();
        foreach (var item in Categories)
        {
            item.ResetScanResult();
        }

        SetItemsEnabled(false);
        ScanButton.Content = "取消扫描";
        CleanButton.IsEnabled = false;
        ScanStateText.Text = "正在后台扫描白名单目录，可以随时取消";
        CleanupFooterText.Text = "正在扫描快速清理项目，请稍候…";
        QuickScanProgressBar.Maximum = Categories.Count;
        QuickScanProgressBar.Value = 0;
        QuickScanProgressBar.Visibility = Visibility.Visible;
        UpdateSummary();

        try
        {
            foreach (var item in Categories)
            {
                _scanCancellation.Token.ThrowIfCancellationRequested();
                item.IsBusy = true;
                item.StatusText = "正在扫描";
                ScanStateText.Text = $"正在扫描 {item.Name}（{QuickScanProgressBar.Value + 1:N0}/{Categories.Count}）";
                try
                {
                    var result = await _cleanupService.ScanAsync(item.Category, _scanCancellation.Token);
                    item.ApplyScanResult(result);
                }
                finally
                {
                    item.IsBusy = false;
                }

                UpdateSummary();
                QuickScanProgressBar.Value++;
            }

            _hasScanned = true;
            SortCategories();
            ScanStateText.Text = "扫描完成 · 推荐项按大小排序，需确认项在底部";
            CleanupFooterText.Text = "错误报告、下载目录和回收站默认不选；可展开判定依据，先查看文件再决定。";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Categories.Where(item => item.StatusText is "正在扫描" or "等待扫描"))
            {
                item.StatusText = "扫描已取消";
            }

            ScanStateText.Text = "扫描已取消 · 请完成重新扫描后再清理";
            CleanupFooterText.Text = "当前为不完整结果，重新扫描完成后可开始清理。";
        }
        catch (Exception exception)
        {
            ScanStateText.Text = "扫描发生错误";
            CleanupFooterText.Text = exception.Message;
            MessageBox.Show(exception.Message, "磁盘清理扫描失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isScanning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            QuickScanProgressBar.Visibility = Visibility.Collapsed;
            SetItemsEnabled(true);
            ScanButton.Content = "重新扫描";
            RefreshDiskSpace();
            UpdateSummary();
        }
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isCleaning || !_hasScanned)
        {
            return;
        }

        // 先清空已有回收站，再移入下载文件，保留本次下载清理的可恢复性。
        var selected = Categories.Where(item => item.IsSelected && item.SizeBytes > 0)
            .OrderBy(item => item.Category.IsRecycleBin ? 0 : 1)
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("请先选择有可清理内容的项目。", "磁盘清理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedSize = selected.Sum(item => item.SizeBytes);
        var selectedNames = string.Join("、", selected.Select(item => item.Name));
        var downloadsNotice = selected.Any(item => item.Category.SendFilesToRecycleBin)
            ? "\n下载文件移入回收站，可恢复，但不会立即释放空间。"
            : string.Empty;
        var recycleNotice = selected.Any(item => item.Category.IsRecycleBin)
            ? "\n回收站内原有文件将永久清空，无法恢复；如同时选择下载目录，先清空回收站，再移入下载文件。"
            : string.Empty;
        var diagnosticNotice = selected.Any(item => item.Category.Id == "crash-reports")
            ? "\n错误报告删除后无法用于排查这些故障；正在排障时请保留。"
            : string.Empty;
        var confirmation = MessageBox.Show(
            $"准备清理：{selectedNames}\n预计处理：{DiskCleanupCategoryViewModel.FormatSize(selectedSize)}{downloadsNotice}{recycleNotice}{diagnosticNotice}\n\n临时文件、缓存和报告直接删除，不能从回收站恢复；清理缓存后首次使用可能变慢。是否继续？",
            "确认磁盘清理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isCleaning = true;
        SetItemsEnabled(false);
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        CleanButton.Content = "正在清理";
        long deletedBytes = 0;
        long recycledBytes = 0;
        var deletedCount = 0;
        var recycledCount = 0;
        var skippedCount = 0;
        string? completionMessage = null;

        try
        {
            foreach (var item in selected)
            {
                item.IsBusy = true;
                item.StatusText = "正在清理";
                CleanupFooterText.Text = $"正在清理 {item.Name}…";
                try
                {
                    var result = await _cleanupService.CleanAsync(item.Category, CancellationToken.None);
                    if (item.Category.SendFilesToRecycleBin)
                    {
                        recycledBytes += result.DeletedBytes;
                        recycledCount += result.DeletedCount;
                    }
                    else
                    {
                        deletedBytes += result.DeletedBytes;
                        deletedCount += result.DeletedCount;
                    }
                    skippedCount += result.SkippedCount;
                    item.StatusText = result.SkippedCount > 0
                        ? $"已清理，{result.SkippedCount:N0} 项被占用或无权限"
                        : "清理完成";
                }
                finally
                {
                    item.IsBusy = false;
                }
            }

            completionMessage = $"永久删除 {DiskCleanupCategoryViewModel.FormatSize(deletedBytes)} / {deletedCount:N0} 个文件；移入回收站 {DiskCleanupCategoryViewModel.FormatSize(recycledBytes)} / {recycledCount:N0} 个文件；跳过 {skippedCount:N0} 项。";
            CleanupFooterText.Text = completionMessage;
            MessageBox.Show(
                completionMessage,
                "磁盘清理完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            CleanupFooterText.Text = "清理过程中发生错误。";
            MessageBox.Show(exception.Message, "磁盘清理失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isCleaning = false;
            SetItemsEnabled(true);
            ScanButton.IsEnabled = true;
            CleanButton.Content = "开始清理";
            UpdateSummary();
        }

        await ScanAllAsync();
        if (completionMessage is not null)
        {
            CleanupFooterText.Text = completionMessage;
        }
    }

    private void OpenCleanupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiskCleanupCategoryViewModel item })
        {
            return;
        }

        if (item.Category.IsRecycleBin)
        {
            OpenCleanupFolder(null, isRecycleBin: true);
            return;
        }

        OpenFolderPaths((Button)sender, item.FolderPaths);
    }

    private static void OpenFolderPaths(Button button, IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            OpenCleanupFolder(paths[0]);
            return;
        }

        if (paths.Count == 0)
        {
            MessageBox.Show("此项目没有可打开的目录。", "打开文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var path in paths)
        {
            var menuItem = new MenuItem
            {
                Header = new TextBlock { Text = path, MaxWidth = 500, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = path
            };
            menuItem.Click += (_, _) => OpenCleanupFolder(path);
            menu.Items.Add(menuItem);
        }

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static void OpenCleanupFolder(string? path, bool isRecycleBin = false)
    {
        try
        {
            if (!isRecycleBin && (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)))
            {
                MessageBox.Show(
                    $"目录尚未生成、已经不存在或当前用户无权访问：\n{path}",
                    "打开文件夹",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = isRecycleBin ? "shell:RecycleBinFolder" : $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "打开文件夹失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CategorySelection_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        if (FoundSummaryText is null || SelectedSummaryText is null || CleanButton is null)
        {
            return;
        }

        var totalBytes = Categories.Sum(item => item.SizeBytes);
        var selectedBytes = Categories.Where(item => item.IsSelected).Sum(item => item.SizeBytes);
        FoundSummaryText.Text = DiskCleanupCategoryViewModel.FormatSize(totalBytes);
        SelectedSummaryText.Text = DiskCleanupCategoryViewModel.FormatSize(selectedBytes);
        var hasRecycleSelection = Categories.Any(item => item.IsSelected && item.SizeBytes > 0 && item.Category.SendFilesToRecycleBin);
        SelectionDetailText.Text = $"{Categories.Count(item => item.IsSelected && item.SizeBytes > 0)} 项" +
                                   (hasRecycleSelection ? " · 移入回收站仍占空间" : " · 缓存删除 / 永久清空");
        CleanButton.IsEnabled = _hasScanned && !_isScanning && !_isCleaning && selectedBytes > 0;
    }

    private void SetItemsEnabled(bool enabled)
    {
        SelectRecommendedButton.IsEnabled = enabled && _hasScanned;
        ClearSelectionButton.IsEnabled = enabled && _hasScanned;
        foreach (var item in Categories)
        {
            item.IsEnabled = enabled && _hasScanned && item.SizeBytes > 0;
        }
    }

    private void SelectRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasScanned || _isScanning || _isCleaning)
        {
            return;
        }

        foreach (var item in Categories)
        {
            item.IsSelected = !item.RequiresConfirmation && item.SizeBytes > 0;
        }

        UpdateSummary();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isCleaning)
        {
            return;
        }

        foreach (var item in Categories)
        {
            item.IsSelected = false;
        }

        UpdateSummary();
    }

    private void SortCategories()
    {
        var sorted = Categories.OrderBy(item => item.RequiresConfirmation)
            .ThenByDescending(item => item.SizeBytes)
            .ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            Categories.Move(Categories.IndexOf(sorted[index]), index);
        }
    }

    private void RefreshDiskSpace()
    {
        try
        {
            var drive = new DriveInfo(SystemDriveRoot);
            DiskSpaceText.Text = $"{drive.Name.TrimEnd('\\')} 系统盘 · 可用空间";
            DiskFreeSpaceText.Text = DiskCleanupCategoryViewModel.FormatSize(drive.AvailableFreeSpace);
            DiskUsageText.Text = $"总容量 {DiskCleanupCategoryViewModel.FormatSize(drive.TotalSize)}";
            DiskUsageProgressBar.Value = drive.TotalSize == 0
                ? 0
                : (drive.TotalSize - drive.TotalFreeSpace) * 100d / drive.TotalSize;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiskSpaceText.Text = "磁盘空间暂不可读取";
            DiskFreeSpaceText.Text = "—";
            DiskUsageText.Text = SystemDriveRoot;
            DiskUsageProgressBar.Value = 0;
        }
    }
}
