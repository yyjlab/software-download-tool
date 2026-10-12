using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SoftwareDownloadCenter.Services;
using SoftwareDownloadCenter.ViewModels;

namespace SoftwareDownloadCenter;

public partial class MainWindow : Window
{
    private enum AppPage
    {
        SoftwareLibrary,
        EnvironmentDeployment,
        DiskCleanup
    }

    private readonly CatalogService _catalogService = new();
    private readonly InstalledSoftwareService _installedSoftwareService = new();
    private readonly SoftwareDownloadService _downloadService = new();
    private readonly InstallerService _installerService = new();
    private readonly SoftwareIconService _iconService = new();
    private readonly ICollectionView _softwareView;
    private bool _isLoading;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _softwareView = CollectionViewSource.GetDefaultView(SoftwareItems);
        _softwareView.Filter = FilterSoftware;
        Loaded += MainWindow_Loaded;
    }

    public ObservableCollection<SoftwareItemViewModel> SoftwareItems { get; } = [];

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private async void SoftwareIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SoftwareItemViewModel item }
            && item.IconSource is null)
        {
            item.IconSource = await _iconService.LoadPackageAsync(item.Package, item.DisplayName);
        }
    }

    private async Task LoadCatalogAsync()
    {
        if (_isLoading)
        {
            return;
        }

        if (SoftwareItems.Any(item => item.IsBusy))
        {
            FooterStatusText.Text = "下载或安装期间不能刷新软件目录，请完成后再刷新。";
            return;
        }

        _isLoading = true;
        FooterStatusText.Text = "正在读取软件目录并检测已安装版本…";

        try
        {
            // 共享目录访问可能受网络和杀毒软件影响，放到后台避免阻塞 WPF UI 线程。
            var packagesTask = Task.Run(() => _catalogService.LoadAsync());
            var installedTask = Task.Run(_installedSoftwareService.GetInstalledSoftware);
            await Task.WhenAll(packagesTask, installedTask);

            SoftwareItems.Clear();
            foreach (var item in (await packagesTask)
                         .Select(package => new SoftwareItemViewModel(package))
                         .OrderBy(item => string.IsNullOrWhiteSpace(item.DisplayVersion)))
            {
                var installed = InstalledSoftwareService.FindMatch(item.Package, await installedTask);
                item.ApplyInstalledVersion(
                    installed is null
                        ? null
                        : string.IsNullOrWhiteSpace(installed.Version) ? "未知版本" : installed.Version);
                SoftwareItems.Add(item);
            }

            _softwareView.Refresh();
            FooterStatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            FooterStatusText.Text = "软件目录加载失败。";
            MessageBox.Show(exception.Message, "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool FilterSoftware(object item)
    {
        if (item is not SoftwareItemViewModel software)
        {
            return false;
        }

        var keyword = SearchTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return software.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || software.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private void DismissSoftwareStatusButton_Click(object sender, RoutedEventArgs e) =>
        FooterStatusText.Text = string.Empty;

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => _softwareView?.Refresh();

    private void SoftwareMenu_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ShowPage(AppPage.SoftwareLibrary);

    private void EnvironmentMenu_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ShowPage(AppPage.EnvironmentDeployment);

    private void DiskCleanupMenu_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ShowPage(AppPage.DiskCleanup);

    private void ShowPage(AppPage page)
    {
        SoftwareLibraryPage.Visibility = page == AppPage.SoftwareLibrary ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentDeploymentPage.Visibility = page == AppPage.EnvironmentDeployment ? Visibility.Visible : Visibility.Collapsed;
        DiskCleanupPage.Visibility = page == AppPage.DiskCleanup ? Visibility.Visible : Visibility.Collapsed;

        SetMenuState(SoftwareMenuBorder, SoftwareMenuText, page == AppPage.SoftwareLibrary);
        SetMenuState(EnvironmentMenuBorder, EnvironmentMenuText, page == AppPage.EnvironmentDeployment);
        SetMenuState(DiskCleanupMenuBorder, DiskCleanupMenuText, page == AppPage.DiskCleanup);
    }

    private static void SetMenuState(Border border, TextBlock text, bool selected)
    {
        border.Tag = selected;
        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "PrimaryBrush" : "TextPrimaryBrush");
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SoftwareItemViewModel item } || item.IsBusy || _isLoading)
        {
            return;
        }

        item.IsBusy = true;
        item.Progress = 0;
        item.ActionText = "正在下载";
        item.StatusText = "正在下载软件安装包";

        try
        {
            var progress = new Progress<double>(value =>
            {
                item.Progress = value;
                item.StatusText = $"下载进度 {value:F0}%";
            });

            var installerPath = await _downloadService.DownloadAsync(
                item.Package,
                progress,
                CancellationToken.None);

            item.ActionText = "正在安装";
            item.StatusText = "下载与校验完成，等待安装结束";
            var installResult = await _installerService.InstallAsync(
                item.Package,
                installerPath,
                CancellationToken.None);

            if (!installResult.Success)
            {
                throw new InvalidOperationException(installResult.Message);
            }

            var installedSoftware = await Task.Run(_installedSoftwareService.GetInstalledSoftware);
            var installed = InstalledSoftwareService.FindMatch(item.Package, installedSoftware);
            var fallbackInstalledVersion = string.IsNullOrWhiteSpace(item.Package.Version)
                ? "已安装"
                : item.Package.Version;
            item.ApplyInstalledVersion(
                installed is null
                    ? fallbackInstalledVersion
                    : string.IsNullOrWhiteSpace(installed.Version) ? fallbackInstalledVersion : installed.Version);
            item.StatusText = installResult.Message;
            FooterStatusText.Text = $"{item.Name}：{installResult.Message}";
        }
        catch (Exception exception)
        {
            item.StatusText = exception.Message;
            item.ActionText = item.IsUpdateAvailable ? "重新更新" : "重试";
            MessageBox.Show(exception.Message, $"{item.Name} 操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            item.IsBusy = false;
            _softwareView.Refresh();
        }
    }
}
