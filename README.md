# 企业软件中心

企业工具中心提供软件下载与更新、开发环境按需部署和 C 盘安全清理，支持共享安装包扫描、已安装版本检测、断点续传和安装包校验。

## 目录结构

```text
软件下载开发
├─ SoftwareDownloadCenter.csproj     WPF 项目配置，指定 .NET 10 和内置配置资源
├─ App.xaml                          程序入口、颜色、按钮等全局界面样式
├─ App.xaml.cs                       WPF 应用程序启动类
├─ MainWindow.xaml                   软件中心主窗口和页面布局
├─ MainWindow.xaml.cs                搜索、刷新、下载、安装等页面交互逻辑
├─ catalog.json                      软件目录入口，配置软件或引用其他目录文件
├─ environments.json                 开发环境模板、固定版本、安装目录和环境变量配置
├─ Assets
│  ├─ app-icon.png                   软件中心页面品牌图标
│  └─ app-icon.ico                   Windows 窗口、任务栏和 EXE 程序图标
├─ Models
│  ├─ DiskCleanupModels.cs            清理分类、白名单目标和扫描结果数据结构
│  ├─ SoftwarePackage.cs             软件目录、软件信息和已安装软件的数据结构
│  └─ EnvironmentProfile.cs          环境模板和部署组件的数据结构
├─ Services
│  ├─ CatalogService.cs              读取本地、共享目录或网页上的 catalog.json
│  ├─ EnvironmentProfileService.cs   读取并校验 environments.json
│  ├─ EnvironmentReleaseResolver.cs  从官方源解析开发环境和工具版本
│  ├─ EnvironmentDeploymentService.cs 下载、部署、设置环境变量并验证结果
│  ├─ DiskCleanupService.cs            扫描并清理内置白名单中的缓存和临时文件
│  ├─ SourceResolver.cs              自动识别网页、本机、UNC共享和相对路径
│  ├─ SoftwareDownloadService.cs     下载或复制安装包、断点续传、进度和 SHA-256/SHA-512 校验
│  ├─ InstalledSoftwareService.cs    从 Windows 注册表检测软件及已安装版本
│  └─ InstallerService.cs            申请管理员权限并执行 MSI/EXE 安装程序
├─ ViewModels
│  ├─ DiskCleanupCategoryViewModel.cs 清理项目的选择、大小和扫描状态
│  ├─ SoftwareItemViewModel.cs       软件卡片状态、版本比较、按钮文字和下载进度
│  └─ EnvironmentComponentViewModel.cs 环境组件的选择、状态和进度
├─ Views
│  ├─ EnvironmentDeploymentView.xaml 环境部署页面
│  └─ DiskCleanupView.xaml            C 盘安全清理页面
├─ README.md                         项目使用和配置说明
├─ bin                               编译结果，正式程序位于 bin\Release 下
└─ obj                               编译过程产生的中间文件，不需要手工修改
```

整体关系可以理解为：

```text
catalog.json
     │
     ▼
CatalogService 读取软件清单
     │
     ▼
主窗口显示软件、已安装版本和更新状态
     │
     ├─ SoftwareDownloadService 下载或复制安装包
     └─ InstallerService 提权并执行安装程序
```

`bin` 和 `obj` 都是编译时自动生成的目录，不属于业务源码。删除后可以通过重新编译生成，不要在里面直接修改程序代码。

## 运行与发布

开发时需要安装 .NET 10 SDK，在项目目录运行：

```powershell
dotnet run
```

仅供本机编译检查时运行：

```powershell
dotnet build -c Release
```

`dotnet build` 的 `bin\Release\net10.0-windows` 下包含 EXE、DLL 和运行时配置，如果使用这个目录，必须整个目录一起复制。

正式分发时只需执行一次下面的单文件发布命令：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
```

发布结果位于 `bin\Release\net10.0-windows\win-x64\publish\SoftwareDownloadCenter.exe`，可以只复制这一个 EXE。默认的 `catalog.json` 和 `environments.json` 已内置在 EXE 中；需要临时修改配置时，可以把同名 JSON 放到 EXE 旁边，删除外部文件后自动恢复使用内置配置。

如果外部 `catalog.json` 使用相对安装包路径，可以在 EXE 旁边创建 `packages`：

```text
net10.0-windows
├─ SoftwareDownloadCenter.exe
├─ catalog.json                      可选，覆盖 EXE 内置软件目录
├─ environments.json                 可选，覆盖 EXE 内置环境部署配置
└─ packages                          可选，用于存放本地安装包
   ├─ WPS_Setup.exe
   └─ Chrome_Setup.msi
```

下载或复制后的安装包缓存位于当前 Windows 用户目录：

```text
%LOCALAPPDATA%\SoftwareDownloadCenter\Cache
```

## 软件目录

在 `catalog.json` 的 `packages` 中配置需要特殊规则的软件。`catalogUrl` 留空时读取当前文件，也可以引用网页、本机文件、局域网共享文件或相对路径中的另一个软件目录。

`autoScanDirectories` 用于自动扫描安装包目录。目录中的顶层 `.exe` 和 `.msi` 文件会自动出现在软件中心；新增文件后点“刷新”即可加入，从目录删除文件后点“刷新”即可从界面移除。网络共享临时不可访问时不会清空原清单。

```json
{
  "catalogUrl": "",
  "autoScanDirectories": [
    "\\\\DC01\\soft"
  ],
  "categoryOverrides": [
    {
      "match": "dingtalk",
      "category": "办公软件",
      "enabled": true
    }
  ],
  "packages": [
    {
      "id": "software-id",
      "name": "软件名称",
      "description": "软件说明",
      "category": "办公软件",
      "version": "1.0.0",
      "downloadUrl": "https://server.example/software/setup.exe",
      "fileName": "setup.exe",
      "sha256": "安装包的 SHA-256",
      "silentInstallArguments": "/S",
      "detectionDisplayNameContains": "控制面板中显示的软件名称",
      "versionDetectionMode": "registry",
      "enabled": true
    }
  ]
}
```

`versionDetectionMode` 用来决定检测到软件后是否比较版本：

- `registry`：默认方式，读取 Windows 卸载注册表中的版本并与 `version` 比较。
- `presenceOnly`：只判断是否已安装，不信任注册表版本；检测到后显示“已安装（版本由软件自身管理）”并禁用安装按钮。适合钉钉这类会自行更新、但卸载注册表版本可能滞后的软件。

`detectionDisplayNameContains` 应填写 Windows“应用和功能”或卸载注册表中的真实名称。例如钉钉的登记名称是 `钉钉`，不要把安装包版本写进这个字段。

自动扫描生成的项目使用安装包文件名作为名称。刷新时不会读取网络 EXE 的版本资源，也不会预先计算 SHA-256，避免共享目录或杀毒检查导致界面长时间卡顿。需要固定名称、版本、检测规则、静默参数或 SHA-256 时，再在 `packages` 中加入对应文件的配置即可。

## 软件列表

所有软件统一显示在“全部”中，不再按分类切换。可通过软件名称或说明搜索，也可勾选“只看可更新”；版本未提供的软件仍集中在列表底部。

## 开发环境部署

左侧“环境部署”读取程序旁边的 `environments.json`（没有外部文件时使用内置配置）。所有组件默认不勾选，只有手动勾选的组件才会部署。默认安装根目录：

```text
D:\software
```

默认策略：

- Java JDK 使用 `latestStable`，从 Eclipse Adoptium 官方源获取最新 GA 的 Windows x64 MSI，并安装为 Eclipse Temurin/OpenJDK。
- Python 使用 `latestStable`，部署前从 Python 官网解析 Windows 64 位最新稳定版。
- NVM for Windows 使用 `latestStable`，部署前从项目官方 GitHub Release 解析 Windows x64 安装程序。
- Node.js 使用 `latestLts`，由 NVM 下载并启用官网最新 LTS，不再单独安装 Node MSI。
- Go 默认不勾选；勾选 Go 后点击“一键部署”，从官网获取最新稳定版 Windows x64 MSI，校验官方 SHA-256，并安装到 `D:\software\Go`。自动配置 PATH，使用 `go version` 验证；不额外设置 GOROOT，也不覆盖已有 GOPATH。官方 MSI 可能替换旧版 Go，并更新系统 PATH，执行前会提示。
- Git 保持固定版本，可按需勾选。
- Maven、pnpm、uv、PowerShell 7 默认不勾选，使用官方 Windows x64 便携包，分别部署到 `D:\software\Maven`、`pnpm`、`uv`、`PowerShell`。Maven 使用稳定的 3.x 发行线，部署前检查 JDK，保留已有 `conf` 配置与 `lib\ext` 扩展；不会自动创建项目或虚拟环境，也不会替换系统 PowerShell 5.1。
- .NET SDK 默认不勾选，获取仍受支持的最新 LTS SDK。使用微软 EXE 安装器，按官方规则安装到系统 `Program Files\dotnet`，不强制改为 D 盘；通过 `dotnet --list-sdks` 检测 SDK，避免把仅有运行时误判为开发环境。
- 新工具的安装包必须提供有效的官方 SHA-256 或 SHA-512；校验失败时停止部署。

点击“一键部署”后，程序先显示本次解析到的准确版本，确认后才会下载和安装。NVM 2 社区版程序本体按官方规则安装在当前用户的 `%LOCALAPPDATA%\Author Software\nvm`，Node 版本数据和 Shim 保存在 `D:\software\NVM` 下。社区版 NVM 以当前普通用户安装，不强制使用管理员身份。

已安装环境采用两级检测：先检查组件的安装目录，再通过当前进程的系统 `Path` 执行版本命令。检测到符合目标版本的现有组件时会直接跳过安装；版本不符合时才执行更新。`pathDetectionExecutable` 可在 `environments.json` 中手工调整。

Java 统一安装到 `D:\software\Java\latest`，并自动设置 `JAVA_HOME` 和用户 `Path`。需要 Java 8、Java 17、Java 21 等指定旧版本的人员，由本人从对应厂商官网下载安装；本工具不自动降级，也不覆盖其手工维护的旧版本目录。

部署完成后重新打开 CMD，再手工验证：

```cmd
java -version
python --version
nvm version
node -v
npm -v
```

环境变量写入当前 Windows 用户的 `Path`；重新打开 CMD、PowerShell 和 IDE 后生效。NVM 2 使用 Shim 模式切换 Node 版本，不需要创建旧版符号链接。

## C 盘安全清理

左侧“系统工具 → 磁盘清理”只扫描程序内置的白名单目录，不会遍历整个 C 盘，也不提供全盘目录排行和个人大文件删除功能。

支持以下项目（年龄均按最后修改时间计算，不代表最后使用时间）：

- 当前用户临时文件：只统计超过 24 小时的文件。
- Windows 临时文件：只统计超过 7 天的文件，清理时无法删除或无权限的文件会跳过。
- Windows 缩略图与图标缓存。
- 应用崩溃转储和 Windows 错误报告：仅处理指定报告目录内超过 7 天的文件，默认不勾选；排障期间请保留。
- DirectX 着色器缓存：仅处理 D3DSCache 内超过 24 小时的文件，清理后首次启动游戏可能因重新编译而变慢。
- 当前用户下载目录：只统计超过 30 天的文件，默认不勾选；手动勾选清理时文件进入回收站，不会直接永久删除。
- C 盘回收站：默认不勾选，必须手工选择。

快速清理顶部显示系统盘可用空间、扫描总量和所选处理量。推荐项按大小排列，错误报告、下载目录和回收站等需确认项放在底部。每项可展开“查看判定依据”，查看路径、文件匹配和年龄条件，也可打开文件夹检查。完整扫描后才能清理，执行前再次确认；临时文件、缓存和报告直接删除，不能从回收站恢复。移入回收站不会立即释放空间；同时选择回收站和下载目录时，先清空原有回收站，再移入下载文件。

不提供浏览器个人资料、Cookie 或密码的清理规则。扫描可以取消，跳过无权限文件和文件／目录链接；不会因为文件大或目录名包含 Cache 就判定为垃圾。

如果程序发生未处理异常，会在 `%LOCALAPPDATA%\SoftwareDownloadCenter\Logs` 写入按日期命名的崩溃日志，并在界面显示错误信息，避免窗口无提示消失。

## 支持的地址写法

`catalogUrl` 和每个软件的 `downloadUrl` 都会自动识别以下写法，不需要另外填写地址类型：

```text
网页地址：      https://server.example/software/setup.exe
本机绝对路径： E:\软件仓库\setup.exe
局域网共享：   \\fileserver\software\setup.exe
相对路径：     packages\setup.exe
文件 URI：     file:///E:/软件仓库/setup.exe
```

相对路径以实际加载的 `catalog.json` 所在位置为基准：共享目录中的 catalog 会引用同一共享目录下的相对文件，网页 catalog 会引用同一网址下的相对文件。本机和共享目录中的安装包也会先复制到客户端缓存，再执行 SHA-256 校验和安装；访问共享目录时使用当前 Windows 用户的权限。

`catalogUrl` 示例：

```json
{
  "catalogUrl": "\\\\fileserver\\software\\catalog.json",
  "packages": []
}
```

没有确认的软件地址、静默参数和 SHA-256 前，不要发布到正式目录。
