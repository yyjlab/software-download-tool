using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SoftwareDownloadCenter.Models;
using SoftwareDownloadCenter.Services;
using SoftwareDownloadCenter.ViewModels;

namespace SoftwareDownloadCenter.Views;

public partial class EnvironmentDeploymentView : UserControl
{
    private readonly EnvironmentProfileService _profileService = new();
    private readonly EnvironmentDeploymentService _deploymentService = new();
    private readonly EnvironmentReleaseResolver _releaseResolver = new();
    private EnvironmentProfileDocument? _document;
    private CancellationTokenSource? _verificationCancellation;
    private bool _isDeploying;

    public EnvironmentDeploymentView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += EnvironmentDeploymentView_Loaded;
    }

    public ObservableCollection<EnvironmentComponentViewModel> Components { get; } = [];

    private async void EnvironmentDeploymentView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_document is not null)
        {
            return;
        }

        try
        {
            _document = await _profileService.LoadAsync();
            OpenInstallRootButton.ToolTip = _document.InstallRoot;
            var profile = _document.Profiles.FirstOrDefault();
            if (profile is null)
            {
                DeploymentFooterText.Text = "未配置可用的环境组件。";
                return;
            }

            Components.Clear();
            foreach (var component in profile.Components)
            {
                Components.Add(new EnvironmentComponentViewModel(component, _document.InstallRoot));
            }

            await VerifyComponentsAsync();
        }
        catch (Exception exception)
        {
            DeploymentFooterText.Text = "配置加载失败。";
            MessageBox.Show(exception.Message, "环境部署", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenInstallRoot_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var directory = _document?.InstallRoot ?? @"D:\software";
        if (!Directory.Exists(directory))
        {
            MessageBox.Show($"目录不存在或无法访问：\n{directory}", "打开文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "打开文件夹失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void VerifyEnvironmentButton_Click(object sender, RoutedEventArgs e) =>
        await VerifyComponentsAsync();

    private async Task VerifyComponentsAsync()
    {
        if (_document is null || _isDeploying)
        {
            return;
        }

        _verificationCancellation?.Cancel();
        var verificationCancellation = new CancellationTokenSource();
        _verificationCancellation = verificationCancellation;
        var itemsToVerify = Components.ToArray();
        DeploymentFooterText.Text = "正在检测…";
        try
        {
            foreach (var item in itemsToVerify)
            {
                verificationCancellation.Token.ThrowIfCancellationRequested();
                item.StatusText = "正在检测";
                item.StatusDetail = string.Empty;
                var verification = await _deploymentService.VerifyAsync(
                    _document,
                    item.Component,
                    verificationCancellation.Token);
                verificationCancellation.Token.ThrowIfCancellationRequested();
                if (verification.Success)
                {
                    item.SetInstalledStatus(verification.Output);
                }
                else
                {
                    item.StatusText = string.IsNullOrWhiteSpace(item.Component.PackagePath)
                      && !EnvironmentReleaseResolver.RequiresResolution(item.Component)
                        ? "未配置安装包"
                        : "未安装";
                    item.StatusDetail = verification.Output;
                }
            }

            if (ReferenceEquals(_verificationCancellation, verificationCancellation))
            {
                DeploymentFooterText.Text = "检测完成。";
            }
        }
        catch (OperationCanceledException) when (verificationCancellation.IsCancellationRequested)
        {
            // 新的检测或部署已接管，不再更新旧结果。
        }
        finally
        {
            if (ReferenceEquals(_verificationCancellation, verificationCancellation))
            {
                _verificationCancellation = null;
            }

            verificationCancellation.Dispose();
        }
    }

    private async void DeployEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _isDeploying)
        {
            return;
        }

        var selectedItems = Components.Where(item => item.IsSelected).ToArray();
        if (selectedItems.Length == 0)
        {
            MessageBox.Show("请至少选择一个需要部署的组件。", "环境部署", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 在第一个 await 前占用部署状态，防止版本获取期间重复进入或刷新检测。
        _isDeploying = true;
        DeployEnvironmentButton.IsEnabled = false;
        RefreshStatusButton.IsEnabled = false;
        _verificationCancellation?.Cancel();
        try
        {
            var missingPackages = new List<EnvironmentComponentViewModel>();
            foreach (var item in selectedItems.Where(item =>
                         string.IsNullOrWhiteSpace(item.Component.PackagePath)
                         && !EnvironmentReleaseResolver.RequiresResolution(item.Component)))
            {
                var verification = await _deploymentService.VerifyAsync(
                    _document,
                    item.Component,
                    CancellationToken.None);
                if (verification.Success)
                {
                    item.SetInstalledStatus(verification.Output);
                }
                else
                {
                    missingPackages.Add(item);
                }
            }

            if (missingPackages.Count > 0)
            {
                foreach (var item in missingPackages)
                {
                    item.StatusText = "未配置安装包";
                }

                var names = string.Join("、", missingPackages.Select(item => item.Name));
                MessageBox.Show(
                    $"以下组件还没有配置公司指定版本的安装包：{names}\n\n请先修改 environments.json。",
                    "无法开始部署",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                DeploymentFooterText.Text = "正在获取版本…";
                foreach (var item in selectedItems.Where(item =>
                             EnvironmentReleaseResolver.RequiresResolution(item.Component)))
                {
                    item.StatusText = "正在获取版本";
                    await _releaseResolver.ResolveAsync(item.Component, CancellationToken.None);
                    item.RefreshResolvedRelease();
                    item.StatusText = "待部署";
                    item.StatusDetail = string.Empty;
                }
            }
            catch (Exception exception)
            {
                DeploymentFooterText.Text = "版本获取失败，未执行安装。";
                MessageBox.Show(
                    exception.Message,
                    "无法获取官方最新版",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var componentNames = string.Join(
                Environment.NewLine,
                selectedItems.Select(item => $"• {item.Name} {item.Component.Version}"));
            var goNotice = selectedItems.Any(item =>
                item.Component.ReleaseProvider.Equals("goOfficial", StringComparison.OrdinalIgnoreCase))
                ? "\n\nGo 使用官方 MSI，可能需要管理员授权；更新时可能替换已有 MSI 安装的 Go，并由安装器更新 PATH。"
                : string.Empty;
            var dotnetNotice = selectedItems.Any(item => item.Component.DeploymentMode.Equals("dotnetSdk", StringComparison.OrdinalIgnoreCase))
                ? $"\n.NET SDK 按微软规则安装到 {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")}，可能需要管理员授权。"
                : string.Empty;
            var mavenNotice = selectedItems.Any(item => item.Component.ReleaseProvider.Equals("mavenOfficial", StringComparison.OrdinalIgnoreCase))
                ? "\nMaven 需要已安装 JDK；已有 Maven 配置和扩展文件将保留。"
                : string.Empty;
            var confirmation = MessageBox.Show(
                $"将按顺序部署：\n{componentNames}\n\n默认安装根目录：{_document.InstallRoot}{goNotice}{dotnetNotice}{mavenNotice}\n\n是否继续？",
                "确认环境部署",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var completedCount = 0;
            foreach (var item in selectedItems)
            {
                item.IsBusy = true;
                item.Progress = 0;
                item.StatusText = "准备部署";
                item.StatusDetail = string.Empty;
                DeploymentFooterText.Text = $"正在部署 {item.Name}…";

                var progress = new Progress<double>(value => item.Progress = value);
                var result = await _deploymentService.DeployAsync(
                    _document,
                    item.Component,
                    progress,
                    status => Dispatcher.Invoke(() => item.StatusText = status),
                    CancellationToken.None);

                item.IsBusy = false;
                item.Progress = result.Success ? 100 : item.Progress;
                if (result.Success)
                {
                    item.SetInstalledStatus(result.VerificationOutput);
                }
                else
                {
                    item.StatusText = "部署失败";
                    item.StatusDetail = result.Message;
                }
                if (!result.Success)
                {
                    DeploymentFooterText.Text = $"{item.Name} 部署失败，已停止后续步骤。";
                    MessageBox.Show(result.Message, $"{item.Name} 部署失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                completedCount++;
            }

            DeploymentFooterText.Text = $"已部署 {completedCount} 项；请重开终端和 IDE。";
            MessageBox.Show(
                "开发环境部署完成。请重新打开 CMD、PowerShell 和 IDE，使新环境变量生效。",
                "环境部署完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            DeploymentFooterText.Text = "环境部署发生错误。";
            MessageBox.Show(exception.Message, "环境部署失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (var item in Components)
            {
                item.IsBusy = false;
            }

            _isDeploying = false;
            DeployEnvironmentButton.IsEnabled = true;
            RefreshStatusButton.IsEnabled = true;
        }
    }
}
