using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class InstallerService
{
    public async Task<InstallResult> InstallAsync(
        SoftwarePackage package,
        string installerPath,
        CancellationToken cancellationToken,
        bool runAsAdministrator = true)
    {
        var extension = Path.GetExtension(installerPath);
        var isMsi = extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isMsi ? "msiexec.exe" : installerPath,
            Arguments = isMsi
                ? $"/i \"{installerPath}\" {package.SilentInstallArguments}".Trim()
                : package.SilentInstallArguments,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        };
        if (runAsAdministrator)
        {
            startInfo.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("无法启动安装程序。");
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode switch
            {
                0 => new InstallResult(true, false, "安装完成"),
                1641 or 3010 => new InstallResult(true, true, "安装完成，需要重启电脑"),
                _ => new InstallResult(false, false, $"安装程序返回错误代码 {process.ExitCode}")
            };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new InstallResult(false, false, "用户取消了管理员授权");
        }
    }
}

public sealed record InstallResult(bool Success, bool RebootRequired, string Message);
