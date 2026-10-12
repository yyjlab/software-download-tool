using System.IO;
using System.Reflection;

namespace SoftwareDownloadCenter.Services;

internal static class EmbeddedConfiguration
{
    private const string ResourcePrefix = "SoftwareDownloadCenter.Config.";

    public static Stream Open(string fileName)
    {
        var externalPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(externalPath))
        {
            return File.OpenRead(externalPath);
        }

        return Assembly.GetExecutingAssembly()
                   .GetManifestResourceStream(ResourcePrefix + fileName)
               ?? throw new FileNotFoundException(
                   $"程序内没有找到默认配置 {fileName}，EXE 旁边也没有外部配置文件。",
                   externalPath);
    }
}
