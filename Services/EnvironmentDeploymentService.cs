using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using SoftwareDownloadCenter.Models;

namespace SoftwareDownloadCenter.Services;

public sealed class EnvironmentDeploymentService
{
    private readonly SoftwareDownloadService _downloadService = new();
    private readonly InstallerService _installerService = new();

    public async Task<EnvironmentDeploymentResult> DeployAsync(
        EnvironmentProfileDocument document,
        EnvironmentComponent component,
        IProgress<double> progress,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        var installDirectory = ResolveComponentInstallDirectory(document.InstallRoot, component);
        var existingVerification = await VerifyAsync(document, component, cancellationToken);
        if (existingVerification.Success)
        {
            return new EnvironmentDeploymentResult(
                true,
                true,
                false,
                $"已安装，跳过：{existingVerification.Output}",
                existingVerification.Output);
        }

        if (component.DeploymentMode.Equals("nodeViaNvm", StringComparison.OrdinalIgnoreCase))
        {
            return await DeployNodeViaNvmAsync(
                document,
                component,
                progress,
                reportStatus,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(component.PackagePath))
        {
            return new EnvironmentDeploymentResult(false, false, false, "未配置安装包地址");
        }

        if (component.ReleaseProvider.Equals("mavenOfficial", StringComparison.OrdinalIgnoreCase))
        {
            var java = document.Profiles.SelectMany(profile => profile.Components)
                .FirstOrDefault(item => item.ReleaseProvider.Equals("javaAdoptium", StringComparison.OrdinalIgnoreCase));
            var dependency = new EnvironmentComponent
            {
                RelativeInstallDirectory = java?.RelativeInstallDirectory ?? "Java\\latest",
                VerifyExecutable = "{InstallDirectory}\\bin\\javac.exe",
                PathDetectionExecutable = "javac.exe",
                VerifyArguments = "-version"
            };
            var javaVerification = await VerifyAsync(document, dependency, cancellationToken);
            if (!javaVerification.Success)
                return new EnvironmentDeploymentResult(false, false, false, "Maven 需要 JDK，请先部署 Java JDK 或将已有 JDK 配置到 PATH。");
        }

        reportStatus("正在复制并校验安装包");
        var fileName = GetPackageFileName(component);
        var package = new SoftwarePackage
        {
            Id = $"environment-{component.Id}",
            Name = component.Name,
            Version = component.Version,
            DownloadUrl = component.PackagePath,
            FileName = fileName,
            Sha256 = component.Sha256,
            Sha512 = component.Sha512,
            SilentInstallArguments = ExpandTokens(component.SilentInstallArguments, document.InstallRoot, installDirectory)
        };

        var installerPath = await _downloadService.DownloadAsync(package, progress, cancellationToken);
        var installerType = component.InstallerType.Trim().ToLowerInvariant();
        var rebootRequired = false;

        if (installerType == "zip")
        {
            reportStatus("正在解压环境文件");
            await Task.Run(
                () => ExtractEnvironmentArchive(installerPath, installDirectory, component, cancellationToken),
                cancellationToken);
        }
        else if (installerType is "exe" or "msi")
        {
            reportStatus("正在运行静默安装程序");
            Directory.CreateDirectory(installDirectory);
            var installResult = await _installerService.InstallAsync(
                package,
                installerPath,
                cancellationToken,
                runAsAdministrator: !component.DeploymentMode.Equals(
                    "nvmWindows",
                    StringComparison.OrdinalIgnoreCase));
            if (!installResult.Success)
            {
                return new EnvironmentDeploymentResult(false, false, false, installResult.Message);
            }

            rebootRequired = installResult.RebootRequired;
        }
        else
        {
            return new EnvironmentDeploymentResult(
                false,
                false,
                false,
                $"不支持安装类型：{component.InstallerType}");
        }

        reportStatus("正在设置当前用户环境变量");
        ApplyEnvironmentVariables(document, component, installDirectory);

        if (component.DeploymentMode.Equals("nvmWindows", StringComparison.OrdinalIgnoreCase))
        {
            reportStatus("正在配置 NVM 存储目录和 Shim 模式");
            var configuration = await ConfigureNvmV2Async(
                document.InstallRoot,
                installDirectory,
                cancellationToken);
            if (!configuration.Success)
            {
                return new EnvironmentDeploymentResult(false, false, rebootRequired, configuration.Message);
            }
        }

        reportStatus("正在验证安装结果");
        var verification = await VerifyAsync(document, component, cancellationToken);
        if (!verification.Success)
        {
            return new EnvironmentDeploymentResult(
                false,
                false,
                rebootRequired,
                $"安装已执行，但验证失败：{verification.Output}",
                verification.Output);
        }

        return new EnvironmentDeploymentResult(
            true,
            false,
            rebootRequired,
            rebootRequired ? "部署完成，需要重启电脑" : "部署和验证完成",
            verification.Output);
    }

    private async Task<EnvironmentDeploymentResult> DeployNodeViaNvmAsync(
        EnvironmentProfileDocument document,
        EnvironmentComponent component,
        IProgress<double> progress,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        var nvmExecutable = ResolveNvmExecutable(document.InstallRoot);
        if (!File.Exists(nvmExecutable))
        {
            return new EnvironmentDeploymentResult(
                false,
                false,
                false,
                "没有检测到 NVM，请先选择并部署 NVM for Windows。");
        }

        var nvmHome = Path.GetDirectoryName(nvmExecutable) ?? AppContext.BaseDirectory;

        reportStatus($"正在通过 NVM 下载 Node.js {component.Version} LTS");
        progress.Report(20);
        var installResult = await RunProcessAsync(
            nvmExecutable,
            $"install {component.Version}",
            nvmHome,
            runAsAdministrator: false,
            cancellationToken);
        if (!installResult.Success)
        {
            return new EnvironmentDeploymentResult(false, false, false, installResult.Message);
        }

        reportStatus($"正在启用 Node.js {component.Version}");
        progress.Report(75);
        var useResult = await RunProcessAsync(
            nvmExecutable,
            $"use {component.Version}",
            nvmHome,
            runAsAdministrator: false,
            cancellationToken);
        if (!useResult.Success)
        {
            return new EnvironmentDeploymentResult(false, false, false, useResult.Message);
        }

        progress.Report(90);
        reportStatus("正在验证 node 和 npm");
        var verification = await VerifyAsync(document, component, cancellationToken);
        if (!verification.Success)
        {
            return new EnvironmentDeploymentResult(
                false,
                false,
                false,
                $"Node.js 已安装，但验证失败：{verification.Output}",
                verification.Output);
        }

        var npmComponent = new EnvironmentComponent
        {
            RelativeInstallDirectory = component.RelativeInstallDirectory,
            VerifyExecutable = "{InstallDirectory}\\npm.exe",
            VerifyArguments = "--version"
        };
        var npmVerification = await VerifyAsync(document, npmComponent, cancellationToken);
        if (!npmVerification.Success)
        {
            return new EnvironmentDeploymentResult(
                false,
                false,
                false,
                $"Node.js 已安装，但 npm 验证失败：{npmVerification.Output}",
                npmVerification.Output);
        }

        progress.Report(100);
        return new EnvironmentDeploymentResult(
            true,
            false,
            false,
            "Node.js LTS 部署和验证完成",
            $"node {verification.Output}；npm {npmVerification.Output}");
    }

    public async Task<EnvironmentVerificationResult> VerifyAsync(
        EnvironmentProfileDocument document,
        EnvironmentComponent component,
        CancellationToken cancellationToken)
    {
        var installDirectory = ResolveComponentInstallDirectory(document.InstallRoot, component);
        var verifyExecutable = ExpandTokens(
            component.VerifyExecutable,
            document.InstallRoot,
            installDirectory);
        var workingDirectory = Directory.Exists(installDirectory)
            ? installDirectory
            : AppContext.BaseDirectory;
        var targets = new List<EnvironmentVerificationTarget>();
        if (!string.IsNullOrWhiteSpace(verifyExecutable) && File.Exists(verifyExecutable))
        {
            targets.Add(new EnvironmentVerificationTarget(verifyExecutable, "指定目录"));
        }

        var pathExecutable = ExpandTokens(
            component.PathDetectionExecutable,
            document.InstallRoot,
            installDirectory);
        if (!string.IsNullOrWhiteSpace(pathExecutable))
        {
            targets.Add(new EnvironmentVerificationTarget(pathExecutable, "系统 PATH"));
        }

        if (targets.Count == 0)
        {
            return new EnvironmentVerificationResult(false, "指定目录和系统 PATH 均未配置检测程序");
        }

        var failures = new List<string>();
        foreach (var target in targets)
        {
            var result = await VerifyTargetAsync(
                target.Executable,
                ExpandTokens(component.VerifyArguments, document.InstallRoot, installDirectory),
                component.VerifyOutputContains,
                workingDirectory,
                cancellationToken,
                component.VerifyOutputIsExact,
                component.VerifyOutputIsPrefix);
            if (result.Success)
            {
                return new EnvironmentVerificationResult(
                    true,
                    $"{result.Output}（{target.Source}）");
            }

            failures.Add($"{target.Source}：{result.Output}");
        }

        return new EnvironmentVerificationResult(false, string.Join("；", failures));
    }

    private static async Task<EnvironmentVerificationResult> VerifyTargetAsync(
        string executable,
        string arguments,
        string requiredOutput,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool exactOutput = false,
        bool prefixOutput = false)
    {
        var isBatch = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                      || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isBatch ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable,
            Arguments = isBatch ? $"/d /s /c \"\"{executable}\" {arguments}\"" : arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动验证程序。");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = string.Join(
                    " ",
                    await standardOutputTask,
                    await standardErrorTask)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            if (process.ExitCode != 0)
            {
                return new EnvironmentVerificationResult(false, $"验证程序返回 {process.ExitCode}：{output}");
            }

            if (!string.IsNullOrWhiteSpace(requiredOutput)
                && !(exactOutput
                    ? output.Equals(requiredOutput, StringComparison.OrdinalIgnoreCase)
                    : prefixOutput
                        ? output.Equals(requiredOutput, StringComparison.OrdinalIgnoreCase)
                          || output.StartsWith(requiredOutput + " ", StringComparison.OrdinalIgnoreCase)
                    : output.Contains(requiredOutput, StringComparison.OrdinalIgnoreCase)))
            {
                return new EnvironmentVerificationResult(false, $"检测到的版本与配置不符：{output}");
            }

            return new EnvironmentVerificationResult(
                true,
                string.IsNullOrWhiteSpace(output) ? "验证通过" : output);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new EnvironmentVerificationResult(false, "验证超时");
        }
        catch (Exception exception)
        {
            TryTerminateProcess(process);
            return new EnvironmentVerificationResult(false, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void ApplyEnvironmentVariables(
        EnvironmentProfileDocument document,
        EnvironmentComponent component,
        string installDirectory)
    {
        foreach (var variable in component.EnvironmentVariables)
        {
            var value = ExpandTokens(variable.Value, document.InstallRoot, installDirectory);
            Environment.SetEnvironmentVariable(variable.Key, value, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(variable.Key, value, EnvironmentVariableTarget.Process);
        }

        var currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var pathParts = currentUserPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var entriesToAdd = new List<string>();

        foreach (var configuredEntry in component.PathEntries)
        {
            var entry = ExpandTokens(configuredEntry, document.InstallRoot, installDirectory).Trim().TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            entriesToAdd.Add(entry);
            if (!pathParts.Any(existing => PathsEqual(existing, entry)))
            {
                pathParts.Add(entry);
            }
        }

        var updatedPath = string.Join(';', pathParts);
        Environment.SetEnvironmentVariable("Path", updatedPath, EnvironmentVariableTarget.User);

        var currentProcessPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) ?? string.Empty;
        var processPathParts = currentProcessPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var entry in entriesToAdd.Where(entry => !processPathParts.Any(existing => PathsEqual(existing, entry))))
        {
            processPathParts.Add(entry);
        }

        Environment.SetEnvironmentVariable(
            "Path",
            string.Join(';', processPathParts),
            EnvironmentVariableTarget.Process);
    }

    private static async Task<ProcessExecutionResult> ConfigureNvmV2Async(
        string installRoot,
        string nvmDataHome,
        CancellationToken cancellationToken)
    {
        var nvmExecutable = ResolveNvmExecutable(installRoot);
        if (!File.Exists(nvmExecutable))
        {
            return new ProcessExecutionResult(false, $"安装完成后没有找到 {nvmExecutable}");
        }

        var nvmHome = Path.GetDirectoryName(nvmExecutable) ?? nvmDataHome;

        var versionsRoot = Path.Combine(installRoot, "NVM", "node-versions");
        Directory.CreateDirectory(versionsRoot);
        var rootResult = await RunProcessAsync(
            nvmExecutable,
            $"config set root=\"{versionsRoot}\"",
            nvmHome,
            runAsAdministrator: false,
            cancellationToken);
        if (!rootResult.Success)
        {
            return rootResult;
        }

        return await RunProcessAsync(
            nvmExecutable,
            "use shim",
            nvmHome,
            runAsAdministrator: false,
            cancellationToken);
    }

    private static async Task<ProcessExecutionResult> RunProcessAsync(
        string executable,
        string arguments,
        string workingDirectory,
        bool runAsAdministrator,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = runAsAdministrator,
            CreateNoWindow = !runAsAdministrator
        };
        if (runAsAdministrator)
        {
            startInfo.Verb = "runas";
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动 NVM。");
            Task<string>? outputTask = null;
            Task<string>? errorTask = null;
            if (!runAsAdministrator)
            {
                outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            }

            await process.WaitForExitAsync(timeout.Token);
            var output = outputTask is null
                ? string.Empty
                : string.Join(" ", await outputTask, await errorTask!).Trim();
            return process.ExitCode == 0
                ? new ProcessExecutionResult(true, output)
                : new ProcessExecutionResult(
                    false,
                    string.IsNullOrWhiteSpace(output)
                        ? $"NVM 返回错误代码 {process.ExitCode}"
                        : $"NVM 返回错误代码 {process.ExitCode}：{output}");
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessExecutionResult(false, "NVM 执行超时，已终止进程。");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new ProcessExecutionResult(false, "用户取消了管理员授权");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveNvmExecutable(string installRoot)
    {
        var processNvmHome = Environment.GetEnvironmentVariable(
            "NVM_HOME",
            EnvironmentVariableTarget.Process);
        var userNvmHome = Environment.GetEnvironmentVariable(
            "NVM_HOME",
            EnvironmentVariableTarget.User);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            processNvmHome,
            userNvmHome,
            Path.Combine(localAppData, "Author Software", "nvm"),
            Path.Combine(installRoot, "NVM")
        };

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var executable = Path.Combine(candidate!, "nvm.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        return Path.Combine(localAppData, "Author Software", "nvm", "nvm.exe");
    }

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = Environment.ExpandEnvironmentVariables(left.Trim().TrimEnd('\\'));
        var normalizedRight = Environment.ExpandEnvironmentVariables(right.Trim().TrimEnd('\\'));
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveComponentInstallDirectory(string installRoot, EnvironmentComponent component) =>
        component.DeploymentMode.Equals("dotnetSdk", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            : ResolveInstallDirectory(installRoot, component.RelativeInstallDirectory);

    private static void ExtractEnvironmentArchive(
        string archivePath, string installDirectory, EnvironmentComponent component, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Select(entry =>
            (Entry: entry, Path: ResolveArchiveDestination(installDirectory, component.ArchiveRoot, entry.FullName))).ToArray();
        // 所有目标先通过路径检查，再写入；不删除现有目录或文件。
        foreach (var item in entries.Where(item => item.Path is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (((item.Entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("安装包包含符号链接，已停止解压。");
            EnsureNoReparsePoints(item.Path!);
        }
        Directory.CreateDirectory(installDirectory);
        foreach (var item in entries.Where(item => item.Path is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(item.Path!);
            if (item.Entry.FullName.EndsWith('/') || item.Entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(item.Path!);
                continue;
            }
            var relativePath = Path.GetRelativePath(installDirectory, item.Path!);
            if (component.ReleaseProvider.Equals("mavenOfficial", StringComparison.OrdinalIgnoreCase)
                && File.Exists(item.Path)
                && (relativePath.StartsWith("conf\\", StringComparison.OrdinalIgnoreCase)
                    || relativePath.StartsWith("lib\\ext\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue; // 保留已有 Maven 配置和扩展，不覆盖用户定制。
            }
            Directory.CreateDirectory(Path.GetDirectoryName(item.Path!)!);
            item.Entry.ExtractToFile(item.Path!, overwrite: true);
        }
    }

    internal static string? ResolveArchiveDestination(string installDirectory, string archiveRoot, string entryName)
    {
        var relativePath = entryName.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(archiveRoot))
        {
            var prefix = archiveRoot.TrimEnd('/', '\\') + "/";
            if (relativePath.Equals(prefix, StringComparison.Ordinal)) return null;
            if (!relativePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("安装包文件不在预期的目录内。");
            relativePath = relativePath[prefix.Length..];
        }
        if (relativePath.Length == 0) return null;
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new InvalidDataException("安装包包含不安全的文件路径。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包文件路径超出部署目录。");
        return destination;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("部署路径包含符号链接或目录联接，已停止解压。");
        }
    }

    private static string ResolveInstallDirectory(string installRoot, string relativeInstallDirectory)
    {
        var normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var installDirectory = string.IsNullOrWhiteSpace(relativeInstallDirectory)
            ? normalizedRoot
            : Path.GetFullPath(relativeInstallDirectory.Trim(), normalizedRoot);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!installDirectory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !installDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装目录必须位于 D:\\software 内。 ");
        }

        return installDirectory;
    }

    private static string ExpandTokens(string value, string installRoot, string installDirectory) =>
        Environment.ExpandEnvironmentVariables(
            (value ?? string.Empty)
            .Replace("{InstallRoot}", installRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{InstallDirectory}", installDirectory, StringComparison.OrdinalIgnoreCase));

    private static string GetPackageFileName(EnvironmentComponent component)
    {
        var source = SourceResolver.Resolve(component.PackagePath, AppContext.BaseDirectory);
        var fileName = source.Kind == SourceKind.Http
            ? Path.GetFileName(source.HttpUri!.LocalPath)
            : Path.GetFileName(source.LocalPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{component.Id}.{component.InstallerType}"
            : fileName;
    }

    private static void TryTerminateProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出，无需处理。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 终止失败时仍返回原始验证错误。
        }
    }
}

public sealed record EnvironmentVerificationResult(bool Success, string Output);
internal sealed record EnvironmentVerificationTarget(string Executable, string Source);
internal sealed record ProcessExecutionResult(bool Success, string Message);
