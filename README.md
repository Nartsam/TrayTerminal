# TrayTerminal

TrayTerminal 是一个 Windows 11 x64 便携目录版终端工具。它把主界面、托盘、多标签管理放在 WPF 程序里，把真正的终端进程放在独立的 `TrayTerminal.Host.exe` 里运行，并通过 ConPTY + 命名管道把输入输出连接到 WebView2 中的 xterm.js。

## 软件功能

- 多标签终端：启动后默认创建一个终端，也可以点击左上角”添加新终端”创建更多标签。
- 终端类型：默认检测 CMD、Windows PowerShell；如果 `pwsh.exe` 在 `PATH` 中，会自动加入 PowerShell 7。
- 管理员终端：新建标签时勾选”以管理员模式启动”，会通过 UAC 启动提权的 Host 进程。
- 标签重命名：双击标签标题可以修改名称。
- 标签背景：在程序运行目录旁手动创建 `Backgrounds` 文件夹，放入与标签名相同的 `.png`、`.jpg`、`.jpeg` 图片，创建或重命名标签时会自动匹配为当前终端背景。匹配顺序为 `.png`、`.jpg`、`.jpeg`。
- 标签预设配置：通过 `Data\Config\config.txt` 为特定名称的标签指定工作目录、自动执行命令、预填命令和背景图片。
- 启动自动建标签：通过 `Data\Config\init.txt` 指定程序启动时自动创建的标签列表。
- 独立字号：右上角字号下拉框只调整当前标签，切换标签时会显示该标签自己的字号。
- 托盘模式：点击”隐藏到托盘”隐藏窗口；托盘图标双击可显示或隐藏窗口；右键菜单会根据当前窗口状态显示”显示”或”隐藏”。
- 关闭行为：点击窗口关闭按钮时会询问是退出程序还是隐藏到托盘；托盘右键”退出”则直接退出。关闭仍在运行的标签或退出程序时会提示确认，确认后会强制结束该终端及子进程。

## 运行时数据

TrayTerminal 的应用自有数据都写在程序运行目录下的 `Data` 文件夹中：

- `Data\Config`：配置目录，存放 `config.txt`（标签预设配置）和 `init.txt`（启动自动建标签）。
- `Data\Logs`：应用和 Host 日志，例如 `app-20260517.log`、`host-20260517.log`。
- `Data\Temp`：预留的临时文件目录。
- `Data\WebView2`：WebView2 用户数据目录，包括缓存、Local State、GPUCache 等浏览器运行数据。

`Backgrounds` 是用户手动创建的可选目录，程序只读取其中同名图片，不会自动创建它。

便携性约定是：TrayTerminal 主动创建的配置、日志、缓存、临时文件都在程序目录内。Windows、.NET、WebView2 Runtime 自身可能存在系统级缓存或安装目录，这些不属于应用可控数据。

## 配置文件

### config.txt

`Data\Config\config.txt` 是标签预设配置文件，YAML 格式。为特定名称的标签指定创建时的预设动作，名称区分大小写。支持的字段：

- `cd`：创建标签时将终端工作目录切换到指定路径。
- `run`：终端启动后自动执行的命令（会自动按下回车）。
- `fill`：终端启动后预填到命令行的内容（不按回车，等待用户手动执行）。`run` 和 `fill` 同时存在时仅 `run` 生效。
- `bg`：终端背景图片路径，支持相对路径（相对于程序运行目录）和绝对路径。优先级高于 `Backgrounds` 目录的同名图片自动匹配；如果指定的文件不存在则不显示任何背景。

示例：

```yaml
MMSys:
  cd: "D:\Program Files\MMSys"
  run: "cmd run.bat"
  bg: "./Backgrounds/1.png"

NapCat:
  cd: "D:\Program Files\NapCat"
  fill: "napcat start"
```

### init.txt

`Data\Config\init.txt` 是启动自动建标签配置文件。程序启动时读取此文件，按顺序自动创建标签。每个非空行表示一个标签，格式为：

```
管理员标志/终端类型/标签名称
```

- 管理员标志：`admin` 表示以管理员模式启动，其他任何内容或空串表示普通模式。
- 终端类型：`cmd`、`powershell`（Windows PowerShell）、`pwsh`（PowerShell 7）。
- 标签名称：创建后的标签标题，会自动应用 `config.txt` 中同名的预设配置。

示例：

```
admin/pwsh/MMSys
/powershell/Test Powershell
```

如果 `init.txt` 不存在或为空，程序启动时会按默认方式创建一个终端标签。

## 项目结构

```text
TrayTerminal.sln
Directory.Build.props
Task.md
icon.png
README.md
scripts/
  publish-portable.ps1
src/
  TrayTerminal.App/
  TrayTerminal.Host/
  TrayTerminal.Shared/
  TrayTerminal.SmokeTests/
```

### 根目录

- `TrayTerminal.sln`：解决方案，包含 App、Host、Shared、SmokeTests 四个项目。
- `Directory.Build.props`：统一目标框架、x64 平台、Nullable、隐式 using 等通用 MSBuild 设置。
- `Task.md`：原始任务说明。
- `icon.png`：应用图标源图；当前 WPF 项目使用已生成的 `Assets/app.ico`。
- `README.md`：项目说明、运行数据、结构和构建说明。
- `.gitignore`：忽略构建输出、发布目录和本地数据。

### `scripts`

- `scripts/publish-portable.ps1`：发布脚本。先发布 Host，再发布 App，最后把 Host 输出复制到 `publish\TrayTerminal`，形成可直接运行的便携目录。

### `src/TrayTerminal.App`

- `TrayTerminal.App.csproj`：WPF 主程序项目，引用 WebView2、Shared，并在构建后复制 Host 输出。
- `app.manifest`：应用清单，声明 Windows 桌面程序运行信息。
- `App.xaml`：全局 WPF 资源和暗色控件样式。
- `App.xaml.cs`：应用启动入口，初始化便携目录、日志、当前工作目录和主窗口。
- `MainWindow.xaml`：主窗口布局，包含顶部工具栏、字号选择、隐藏按钮和标签容器。
- `MainWindow.xaml.cs`：标签创建、重命名、背景匹配、托盘行为、关闭确认等主界面逻辑。
- `Assets/app.ico`：应用窗口、任务栏和托盘图标。
- `Assets/terminal.html`：WebView2 加载的终端页面，承载 xterm.js、键盘输入、resize、背景图和轻量 fallback 渲染。
- `Assets/xterm/xterm.css`：xterm.js 样式文件。
- `Assets/xterm/xterm.js`：xterm.js 脚本文件。
- `Controls/TerminalPage.cs`：单个标签页的 WPF 包装，管理标题、字号、背景、状态栏、TerminalSession 和 TerminalView。
- `Controls/TerminalView.cs`：WebView2 终端控件，负责加载 `terminal.html`，把前端输入/resize 转给后端，把后端输出写回 xterm.js。
- `Dialogs/AppMessageDialog.xaml`：自定义消息对话框界面，替代系统 MessageBox（无系统音效）。
- `Dialogs/AppMessageDialog.xaml.cs`：自定义消息对话框逻辑，支持确认、提示和多选操作。
- `Dialogs/NewTerminalDialog.xaml`：新建终端对话框界面。
- `Dialogs/NewTerminalDialog.xaml.cs`：新建终端对话框逻辑，生成 `NewTerminalRequest`。
- `Dialogs/NewTerminalRequest.cs`：创建终端所需的标题、Profile、管理员标志。
- `Dialogs/RenameTabDialog.xaml`：重命名标签对话框界面。
- `Dialogs/RenameTabDialog.xaml.cs`：重命名标签对话框逻辑和空名称校验。
- `Services/InitConfig.cs`：解析 `Data\Config\init.txt`，返回启动时需自动创建的标签列表。
- `Services/TabPresetConfig.cs`：解析 `Data\Config\config.txt`，返回按标签名索引的预设配置。
- `Services/TerminalSession.cs`：主进程侧终端会话，启动普通或管理员 Host，维护命名管道 IPC，发送输入/resize/kill 并接收输出/退出/错误。

### `src/TrayTerminal.Host`

- `TrayTerminal.Host.csproj`：隐藏窗口的 Host 项目，输出 `TrayTerminal.Host.exe`。
- `Program.cs`：Host 入口，解析参数，连接主进程命名管道，完成 nonce 握手，然后启动终端会话。
- `HostOptions.cs`：Host 命令行参数模型和解析逻辑，Base64 编码 shell 路径、参数和工作目录，避免空格与特殊字符破坏参数传递。
- `Terminal/NativeMethods.cs`：ConPTY、CreateProcess、Job Object 等 Win32 API P/Invoke 声明和辅助方法。
- `Terminal/ConPtySession.cs`：创建伪控制台、启动 shell、处理 resize、等待退出、强制结束进程树。
- `Terminal/TerminalHostSession.cs`：Host 侧会话调度器，在 ConPTY 和命名管道之间泵入输入、输出、resize、kill、exit 消息。

### `src/TrayTerminal.Shared`

- `TrayTerminal.Shared.csproj`：App、Host、SmokeTests 共享库项目。
- `FileLogger.cs`：简单文件日志器，把日志写入 `Data\Logs`。
- `PortablePaths.cs`：便携路径中心，确保应用自有路径都落在程序目录下。
- `Ipc/IpcMessage.cs`：IPC 消息结构，包含类型和二进制载荷。
- `Ipc/IpcMessageType.cs`：IPC 消息类型枚举，例如 Hello、Input、Output、Resize、Exit、Kill。
- `Ipc/IpcProtocol.cs`：IPC 二进制帧协议的读写和常用载荷编码。
- `Terminal/TerminalProfile.cs`：终端 Profile 模型，描述显示名、可执行文件、启动参数和工作目录。
- `Terminal/TerminalProfileCatalog.cs`：检测可用终端类型，当前支持 CMD、Windows PowerShell、PowerShell 7。

### `src/TrayTerminal.SmokeTests`

- `TrayTerminal.SmokeTests.csproj`：轻量 smoke test 控制台项目。
- `Program.cs`：测试便携路径边界、IPC 编解码、命名管道帧读写。

### 构建输出

- `src/**/bin`、`src/**/obj`：SDK 构建中间文件和调试输出。
- `publish\host`：发布脚本产生的 Host 临时发布目录。
- `publish\TrayTerminal`：最终便携发布目录，运行 `publish\TrayTerminal\TrayTerminal.exe` 即可启动。

## 构建环境

需要：

- Windows 11 x64。
- .NET 10 SDK x64，且包含 Windows Desktop/WPF 构建能力。
- WebView2 NuGet 包会在 restore 时下载；因此首次构建需要能访问 NuGet。

检查 SDK：

```powershell
dotnet --list-sdks
dotnet --info
```

如果缺少 .NET 10 SDK：

- 推荐从 Microsoft .NET 官网下载安装 .NET 10 SDK x64。
- 也可以使用 winget 搜索并安装对应 SDK 包：

```powershell
winget search ".NET SDK 10"
```

安装后重新打开 PowerShell，确认 `dotnet --list-sdks` 能看到 `10.x.x`。

## 构建方法

还原依赖：

```powershell
dotnet restore TrayTerminal.sln
```

调试构建：

```powershell
dotnet build TrayTerminal.sln -c Debug -p:Platform=x64
```

运行 smoke tests：

```powershell
dotnet run --project src\TrayTerminal.SmokeTests\TrayTerminal.SmokeTests.csproj -c Debug -p:Platform=x64
```

发布便携目录：

```powershell
.\scripts\publish-portable.ps1
```

发布成功后产物在：

```text
publish\TrayTerminal\TrayTerminal.exe
```

如果发布脚本提示 `TrayTerminal is running from ...`，说明当前发布目录里的程序还在运行。先从托盘退出 TrayTerminal，再重新执行发布脚本。

## 运行环境

运行 `publish\TrayTerminal\TrayTerminal.exe` 需要：

- Windows 11 x64。
- .NET 10 Desktop Runtime x64。
- Microsoft Edge WebView2 Runtime。
- 至少存在 CMD 或 Windows PowerShell；如果需要 PowerShell 7，需要安装 PowerShell 7 并让 `pwsh.exe` 位于 `PATH`。

如果缺少运行时：

- 缺少 .NET：启动时通常会提示需要安装 `.NET Desktop Runtime`，安装 .NET 10 Desktop Runtime x64 后重试。
- 缺少 WebView2：WebView2 初始化会失败或窗口无法显示终端页面，安装 Microsoft Edge WebView2 Runtime 后重试。
- 缺少 PowerShell 7：软件仍可使用 CMD/Windows PowerShell；安装 PowerShell 7 后重启软件即可自动检测。
- 管理员终端无法启动：检查 UAC 是否被取消、Host 是否被安全软件拦截，以及 `Data\Logs\host-*.log` 中的错误信息。

## 维护提示

- App 与 Host 必须在同一目录下运行，主程序通过 `PortablePaths.HostExecutablePath` 查找 `TrayTerminal.Host.exe`。
- App 与 Host 的 IPC 协议在 `TrayTerminal.Shared.Ipc`，改协议时要同时检查 `TerminalSession` 和 `TerminalHostSession`。
- ConPTY 相关句柄和进程生命周期集中在 `ConPtySession`，这里的释放顺序会影响关闭标签后子进程是否残留。
- WebView2 的用户数据目录固定在 `Data\WebView2`，不要改回系统默认目录，否则会破坏便携数据约定。
- `terminal.html` 同时支持 xterm.js 和 fallback 渲染；如果 xterm 静态文件缺失，仍能看到输出并测试 IPC。
