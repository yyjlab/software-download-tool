using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SoftwareDownloadCenter;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftwareDownloadCenter",
        "Logs");

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog("WPF UI 未处理异常", e.Exception);
        MessageBox.Show(
            $"程序遇到异常，但已阻止窗口直接退出。\n\n错误：{e.Exception.Message}\n日志：{logPath}",
            "企业工具中心",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog("进程未处理异常", exception);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("后台任务未观察异常", e.Exception);
        e.SetObserved();
    }

    private static string WriteCrashLog(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var content = new StringBuilder()
                .AppendLine(new string('=', 80))
                .AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"来源：{source}")
                .AppendLine($"版本：{typeof(App).Assembly.GetName().Version}")
                .AppendLine(exception.ToString())
                .ToString();
            File.AppendAllText(logPath, content, Encoding.UTF8);
            return logPath;
        }
        catch
        {
            return "日志写入失败";
        }
    }
}
