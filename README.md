# TrayTerminal

TrayTerminal 是一个 Windows 11 x64 便携目录版终端工具。它把主界面、托盘、多标签管理放在 WPF 程序里，把真正的终端进程放在独立的 `TrayTerminal.Host.exe` 里运行，并通过 ConPTY + 命名管道把输入输出连接到 WebView2 中的 xterm.js。

## 软件功能

- 多标签终端：启动后默认创建一个终端，也可以点击左上角”添加新终端”创建更多标签。
- 终端类型：默认检测 CMD、Windows PowerShell；如果 `pwsh.exe` 在 `PATH` 中，会自动加入 PowerShell 7。
- 管理员终端：新建标签时勾选”以管理员模式启动”，会通过 UAC 启动提权的 Host 进程。
- 标签排序：拖动顶部标签可以调整终端在界面中的显示顺序。
- 标签重命名：双击标签标题可以修改名称。
- 标签背景：应用会自动创建 `Data\Backgrounds` 文件夹。放入与标签名相同的 `.png`、`.jpg`、`.jpeg` 图片后，创建或重命名标签时会自动匹配为当前终端背景。匹配顺序为 `.png`、`.jpg`、`.jpeg`；旧的程序根目录 `Backgrounds` 不再作为同名图片的自动匹配目录。
- 壁纸重载：右上角“重载壁纸”按钮会重新读取当前标签在 `config.txt` 中的 `bg`、`cover` 和 `Data\Backgrounds` 同名图片设置，不会重新应用 `cd`、`run` 或 `fill`。找不到目标图片时会清除当前壁纸；读取失败时会保留原壁纸。
- 标签预设配置：通过 `Data\Config\config.txt` 为特定名称的标签指定工作目录、自动执行命令、预填命令、背景图片和背景遮罩；创建、关闭、重命名标签等相关操作前会重新加载配置。
- 启动自动建标签：通过 `Data\Config\init.txt` 指定程序启动时自动创建的标签列表。
- 独立字号：右上角字号下拉框只调整当前标签，切换标签时会显示该标签自己的字号。
- 托盘模式：点击”隐藏到托盘”隐藏窗口；托盘图标双击可显示或隐藏窗口；右键菜单会根据当前窗口状态显示”显示”或”隐藏”。
- 关闭行为：点击窗口关闭按钮时会询问是退出程序还是隐藏到托盘；托盘右键”退出”则直接退出。关闭仍在运行的标签或退出程序时会提示确认，确认后会强制结束该终端及子进程。
- 远程访问：可通过浏览器在同一局域网内的另一台设备上查看和操作指定的终端标签。需在 `settings.txt` 中配置端口、令牌和允许的终端名称后启用。

## 运行时数据

TrayTerminal 的应用自有数据都写在程序运行目录下的 `Data` 文件夹中：

- `Data\Config`：配置目录，存放 `settings.txt`（全局设置）、`config.txt`（标签预设配置）和 `init.txt`（启动自动建标签）。
- `Data\Backgrounds`：自动创建的标签背景目录，存放按标签名匹配的 `.png`、`.jpg`、`.jpeg` 图片。
- `Data\Logs`：应用和 Host 日志，例如 `app-20260517.log`、`host-20260517.log`。每天生成一个新文件，程序启动时及跨天滚动时自动清理过期旧日志（默认保留 **30 天**，可在 `settings.txt` 中配置）。
- `Data\Temp`：预留的临时文件目录。
- `Data\WebView2`：WebView2 用户数据目录，包括缓存、Local State、GPUCache 等浏览器运行数据。

便携性约定是：TrayTerminal 主动创建的配置、日志、缓存、临时文件都在程序目录内。Windows、.NET、WebView2 Runtime 自身可能存在系统级缓存或安装目录，这些不属于应用可控数据。

## 配置文件

### config.txt

`Data\Config\config.txt` 是标签预设配置文件，YAML 格式。为特定名称的标签指定创建时的预设动作，名称区分大小写。程序会在创建、关闭、重命名标签以及点击“重载壁纸”等可能用到预设配置的操作前重新读取此文件，因此保存后不需要重启应用。支持的字段：

- `cd`：创建标签时将终端工作目录切换到指定路径。
- `run`：终端启动后自动执行的命令（会自动按下回车）。
- `fill`：终端启动后预填到命令行的内容（不按回车，等待用户手动执行）。`run` 和 `fill` 同时存在时仅 `run` 生效。
- `bg`：终端背景图片路径，支持相对路径（仍相对于程序运行目录）和绝对路径。优先级高于 `Data\Backgrounds` 目录的同名图片自动匹配；如果指定的文件不存在则不显示任何背景。
- `cover`：背景图遮罩强度，合法值是 `0` 到 `100` 的整数。`0` 表示原图，`100` 表示几乎全黑但仍能隐约看到原图。未设置或非法值按 `0` 处理。

无论通过同名自动匹配还是 `bg` 指定，单张背景图片的硬上限都是 **32 MiB**。超限或读取失败时不会替换当前壁纸。

配置文件读取会尽量容错：文件不存在、暂时不可读、正在保存导致读取失败时，本次按空配置处理并继续运行；空标签名会被跳过；非法或不存在的 `cd` 会被剔除并使用终端 Profile 的默认工作目录；非法 `bg` 会被忽略；格式不符合字段要求的行会被跳过；section 内部的空行会被安全忽略，不会截断后续属性。

示例：

```yaml
MMSys:
  cd: "D:\Program Files\MMSys"
  run: "cmd run.bat"
  bg: "./Data/Backgrounds/1.png"
  cover: 45

NapCat:
  cd: "D:\Program Files\NapCat"
  fill: "napcat start"
```

### init.txt

`Data\Config\init.txt` 是启动自动建标签配置文件。程序启动时读取此文件，按顺序自动创建标签。它只影响启动时自动创建的标签；应用运行期间修改后，需要下次启动才会改变启动标签列表。每个非空行表示一个标签，格式为：

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

`init.txt` 读取同样会容错：文件不存在、暂时不可读或包含非法行时不会导致启动失败；非法行会被跳过。

### settings.txt

`Data\Config\settings.txt` 是全局设置配置文件。程序启动时读取一次，运行期间不再重读（修改后需重启应用生效）。文件不存在时所有设置使用默认值。格式为简单的 `key: value`，每行一项，支持 `#` 开头的注释行。

支持的设置项：

- `log_retention_days`：日志保留天数，必须是正整数。未设置或非法值时使用默认值 `30`。
- `remote_port`：远程访问 HTTP 服务端口，必须是正整数。默认值 `8848`，程序启动时始终监听该端口。
- `remote_token`：远程访问令牌（可选）。设置后浏览器必须在 URL 中提供 `?token=<值>` 才能访问；不设置或为空时无需令牌即可访问。
- `remote_allowed_tabs`：允许远程访问的终端名称列表，用竖线 `|` 分隔，名称前后空白会被去除。不设置或为空时远程访问服务不会启动。名称匹配区分大小写（与 `config.txt` 的 section 名称一致）。

示例：

```
# TrayTerminal 全局设置
log_retention_days: 60
remote_port: 8443
remote_token: my_secret_token
remote_allowed_tabs: ttyd | mmsys |
```

## 远程访问

TrayTerminal 支持通过浏览器在同一局域网内远程查看和操作指定的终端标签。

### 启用

在 `Data\Config\settings.txt` 中配置以下三项并重启应用：

- `remote_port`：监听端口（如 `8443`）
- `remote_token`：访问令牌（可选，留空则不验证令牌）
- `remote_allowed_tabs`：允许远程访问的终端名称，用 `|` 分隔（如 `ttyd | mmsys |`）

配置示例已在上方 settings.txt 文档中给出。

应用启动时只有在 `remote_allowed_tabs` 至少包含一个标签名时才会启动 HTTP 服务。如果端口被占用，应用会提示错误并退出。

### 访问

在局域网内另一台设备的浏览器中输入：

```
http://<本机IP>:<端口>/<终端名称>?token=<令牌>
```

例如：

```
http://192.168.1.100:8443/ttyd?token=my_secret_token
```

- 终端名称必须已存在于应用的标签栏中且正在运行，大小写敏感
- 如果未配置令牌，可省略 `?token=` 部分，直接访问 `http://192.168.1.100:8443/ttyd`
- 令牌错误或终端不在允许列表中会返回 403
- 终端未运行会返回 404
- 同一终端最多同时连接 8 个浏览器窗口，整个应用最多 32 个远程连接
- 远程页面支持 `Ctrl+C` / `Ctrl+V`：有选区时 `Ctrl+C` 复制，无选区时仍向终端发送中断信号；`Ctrl+V` 使用浏览器原生粘贴事件，因此在局域网 HTTP 页面中不依赖受限的 Clipboard API
- 新连接和重连会先恢复权威 xterm.js 状态快照，再按单调输出序列衔接快照后的输出与实时输出；浏览器会校验每个序列号，发现缺口时主动断开重连，不会继续显示一个悄悄损坏的终端状态
- 浏览器输入带有 `inputId`。只有 Host 已把对应字节写入 ConPTY 输入管道后，页面才会收到成功确认；如果前台程序暂时不读取输入，页面会明确提示“已写入但屏幕尚无变化”，不会在浏览器中伪造本地回显

### 限制

- **快照历史有界**：远程连接会恢复当前屏幕、终端模式及最近最多 256 行滚动历史，而不是保存进程启动以来的无限原始输出。本地终端仍保留 5000 行滚动缓冲
- **灾难性恢复失败需要重建标签页**：每个会话最多保留 8 MB 快照，以及最多 8 MB、4096 块的快照后输出。WebView2 正常崩溃时会从有效快照和完整尾流自动重建；但如果标签不可见期间权威渲染器崩溃，且尾流在它恢复前超过任一硬上限，旧快照会被原子标记为无效。此时已经没有任何组件持有可证明连续的 VT 状态，本地会提示“终端输出已中断，建议重建标签页”，远程页面会停止重连并提示在主程序中重建该标签页，绝不会从空白或有缺口的状态继续
- **尺寸只有一个 owner**：本地终端可见时由本地窗口决定 ConPTY 尺寸；应用隐藏到托盘或标签不可见时，由最早连接的远程浏览器持有尺寸租约。owner 断开后租约按连接顺序移交，接受的尺寸会广播给本地和所有远程渲染器，非 owner 的 resize 请求只作为其将来接管时的候选尺寸
- **无输入冲突处理**：本地和远程可以同时在同一个终端中输入，等价于两个键盘同时插在一台电脑上。若需协调操作，请自行约定
- **慢速/失联客户端会被自动断开**：每个远程状态订阅和 WebSocket 发送队列都是 512 块的硬上限，单次发送超时 30 秒，同时启用 WebSocket 心跳检测（每 30 秒 ping，20 秒内未收到 pong 即断开）。任一队列写满、序列不连续或连接失联都会主动断开，以保证主程序长期运行时内存不会无界增长；普通断线时浏览器页面会自动尝试重连（最多 5 次，刷新页面可重置）
- **HTTP 请求总数有硬上限**：从 TCP 接受开始计算，整个服务最多同时处理 64 个请求（其中 WebSocket 仍受 32 个连接上限约束）；请求头必须在 10 秒内读完，超限的新连接立即关闭，慢速请求不会形成无界任务或长期占满服务
- **终端结束会断开远程连接**：终端进程退出、标签被关闭或应用正在退出时，服务端会主动关闭对应的远程 WebSocket，避免浏览器窗口继续持有已经结束的终端会话
- **大输入会分片发送**：本地和远程粘贴会在主程序到 Host 的 IPC 层按 64 KB 分片并逐块等待 ConPTY 写入确认；单个远程输入限制为 1 MB，任一输入操作限制为 4 MB，远程单条 WebSocket 消息限制为 16 MB。每个会话最多保留 64 个、合计 16 MB 的等待输入操作，超限会立即失败而不是继续占用内存
- **不显示背景图**：远程浏览器不接收本机的 `Data\Backgrounds` 图片，因此不会显示终端背景图
- **HTTP 明文传输**：所有数据（包括令牌）通过 HTTP 明文传输。该功能设计用于可信局域网环境，不建议暴露到公网
- **浏览器依赖**：需要浏览器支持 WebSocket。所有现代浏览器均支持

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
- `scripts/test-remote-protocol.cjs`：使用 Node.js 验证远程浏览器必须等待 xterm 写入回调，才能依次处理 size 和 `syncComplete`。

### `src/TrayTerminal.App`

- `TrayTerminal.App.csproj`：WPF 主程序项目，引用 WebView2、Shared，并在构建后复制 Host 输出。
- `app.manifest`：应用清单，声明 Windows 桌面程序运行信息。
- `App.xaml`：全局 WPF 资源和暗色控件样式。
- `App.xaml.cs`：应用启动入口，初始化便携目录、日志、当前工作目录和主窗口。
- `MainWindow.xaml`：主窗口布局，包含顶部工具栏、字号选择、隐藏按钮和标签容器。
- `MainWindow.xaml.cs`：标签创建、重命名、配置重载、背景匹配、托盘行为、关闭确认等主界面逻辑。
- `Assets/app.ico`：应用窗口、任务栏和托盘图标。
- `Assets/terminal.html`：WebView2 加载的终端页面，承载 xterm.js、键盘输入、resize、背景图和轻量 fallback 渲染。
- `Assets/remote-protocol.js`：远程页面的逐连接串行消息处理器，保证异步 xterm 写入不会被后续协议消息越过。
- `Assets/xterm/xterm.css`、`xterm.js`：锁定的 `@xterm/xterm` 5.5.0 官方静态文件。
- `Assets/xterm/addon-serialize.js`：锁定的 `@xterm/addon-serialize` 0.13.0，用于生成可恢复 VT 快照；具体来源、SHA-256 和 MIT 许可证见同目录 `THIRD-PARTY-NOTICES.md`、`LICENSE`。
- `Controls/TerminalPage.cs`：单个标签页的 WPF 包装，管理标题、字号、背景、状态栏、TerminalSession 和 TerminalView。
- `Controls/TerminalView.cs`：WebView2 终端控件，负责加载 `terminal.html`，把前端输入/resize 转给后端，把后端输出写回 xterm.js。
- `Dialogs/AppMessageDialog.xaml`：自定义消息对话框界面，替代系统 MessageBox（无系统音效）。
- `Dialogs/AppMessageDialog.xaml.cs`：自定义消息对话框逻辑，支持确认、提示和多选操作。
- `Dialogs/NewTerminalDialog.xaml`：新建终端对话框界面。
- `Dialogs/NewTerminalDialog.xaml.cs`：新建终端对话框逻辑，生成 `NewTerminalRequest`。
- `Dialogs/NewTerminalRequest.cs`：创建终端所需的标题、Profile、管理员标志。
- `Dialogs/RenameTabDialog.xaml`：重命名标签对话框界面。
- `Dialogs/RenameTabDialog.xaml.cs`：重命名标签对话框逻辑和空名称校验。
- `Services/InitConfig.cs`：容错解析 `Data\Config\init.txt`，返回启动时需自动创建的标签列表。
- `Services/TabPresetConfig.cs`：容错解析 `Data\Config\config.txt`，返回按标签名索引的预设配置。
- `Services/RemoteSettings.cs`：读取 `settings.txt` 中的远程访问配置（端口、令牌、允许列表）。
- `Services/WebView2EnvironmentManager.cs`：复用进程级 WebView2 环境，在共享浏览器进程退出后使旧环境失效，供终端视图重建恢复。
- `Services/RemoteAccessService.cs`：内置轻量 HTTP + WebSocket 服务器，管理远程访问生命周期；只依赖 .NET 桌面运行时，不依赖 ASP.NET Core Runtime。
- `Services/RemoteTerminalBridge.cs`：单个远程浏览器 WebSocket 连接到终端会话的桥接器。
- `Services/TerminalSession.cs`：主进程侧终端会话，启动普通或管理员 Host，维护命名管道 IPC，发送输入/resize/kill 并接收输出/退出/错误。
- `Assets/remote-terminal.html`：远程浏览器加载的 xterm.js 终端页面，通过 WebSocket 通信。

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
- 反复提示安装 .NET：先确认安装的是 **x64** 的 .NET 10 Desktop Runtime，而不是仅安装 SDK、普通 Runtime 或 x86 Runtime；可在目标机器运行 `dotnet --list-runtimes`，应能看到 `Microsoft.WindowsDesktop.App 10.x.x [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]`。TrayTerminal 的发布产物不应要求 `Microsoft.AspNetCore.App`，发布脚本会检查这一点。
- 缺少 WebView2：WebView2 初始化会失败或窗口无法显示终端页面，安装 Microsoft Edge WebView2 Runtime 后重试。
- 缺少 PowerShell 7：软件仍可使用 CMD/Windows PowerShell；安装 PowerShell 7 后重启软件即可自动检测。
- 管理员终端无法启动：检查 UAC 是否被取消、Host 是否被安全软件拦截，以及 `Data\Logs\host-*.log` 中的错误信息。

## 维护提示

- App 与 Host 必须在同一目录下运行，主程序通过 `PortablePaths.HostExecutablePath` 查找 `TrayTerminal.Host.exe`。
- `config.txt` 不应作为长期缓存使用；凡是创建、关闭、重命名标签等可能受预设影响的操作，都应通过 `MainWindow` 的配置刷新逻辑读取最新配置。解析器以 section header（顶格、以 `:` 结尾的行）和缩进属性（`key: value`）为单位组织配置；section 内部的空行会被忽略，不会截断后续属性，但属性行必须保持缩进，否则会被误判为新 section。
- App 与 Host 的 IPC 协议在 `TrayTerminal.Shared.Ipc`，改协议时要同时检查 `TerminalSession` 和 `TerminalHostSession`。
- ConPTY 相关句柄和进程生命周期集中在 `ConPtySession`，这里的释放顺序会影响关闭标签后子进程是否残留。
- `ConPtySession.Start` 在每个 Win32 创建步骤之间都必须保持失败路径可释放：管道句柄、伪控制台、attribute list、Job Object 和 FileStream 的所有权转移不要简化，否则反复创建失败的终端可能泄漏句柄。
- WebView2 的用户数据目录固定在 `Data\WebView2`，不要改回系统默认目录，否则会破坏便携数据约定。
- `terminal.html` 保留轻量 fallback 用于显示静态加载错误，但可恢复状态依赖锁定版本的 xterm.js + SerializeAddon；这两个静态文件缺失或不兼容时终端初始化必须失败，不能让不完整的 fallback 状态冒充权威状态。
- 终端背景图只从 `Data\Backgrounds` 自动匹配；`config.txt` 的相对 `bg` 路径仍相对于程序目录。图片读取完成并确认不超过 32 MiB 后，才会通过 `data:` URL 原子应用到 WebView2；读取、大小检查或渲染失败会保留旧壁纸，避免重载竞态和浏览器缓存造成状态不一致。
- 终端输出由 `TerminalStateHub` 分配单调序列，并保存最多 8 MB 的最新 xterm.js SerializeAddon 快照，以及最多 8 MB、4096 块的快照后尾流；双重上限避免极端细碎输出的对象开销突破内存边界。`TerminalView` 的 1024 块有界队列由单个泵任务在 UI 线程上合批调用 `ExecuteScriptAsync`；队列满时后续序列检查会强制从快照重建，不能使用 `DropOldest` 静默跳过 VT 字节。快照生成至少间隔 2 秒，并仅序列化最近 256 行滚动历史，单个快照硬限制 8 MB。
- `TerminalView` 订阅了 `CoreWebView2.ProcessFailed`：渲染进程崩溃（`RenderProcessExited`）和本地逻辑序列缺口会重建当前控件，并从有效快照+完整尾流恢复，但不会错误地使所有标签共享的浏览器环境失效；只有主浏览器进程退出（`BrowserProcessExited`）或脚本调用返回 `RPC_E_DISCONNECTED` 时才会使旧环境失效。不可见标签仍会把 WebView2 重建延迟到再次选中；若此期间尾流超过硬上限，状态已经不可重建，必须明确提示用户重建标签页，不能无限重试或从空状态冒充权威状态。首次初始化及断连重试仍有 30 秒 `ready` 超时，避免页面异常时永久挂起。
- 远程访问功能通过 `RemoteAccessService` 内置的轻量 TCP HTTP/WebSocket 服务实现，避免把 `Microsoft.AspNetCore.App` 变成目标机器的额外运行时依赖。远程协议版本为 2：按 `syncStart → snapshot → replay/output → syncComplete → 当前权威尺寸` 建立状态，之后每个 `output` 必须连续；浏览器按 WebSocket 世代串行处理消息，并等待 xterm `write` 回调后才推进序列或处理 size/`syncComplete`，旧连接回调不能污染重连后的状态。`TerminalStateSubscription` 在断开时必须释放，以免会话继续持有浏览器客户端。
- `TerminalSession` 暴露 `Completion`/`Terminated` 用于终端生命周期收口。正常退出、读循环结束、关闭标签、应用退出和启动失败清理都必须触发完成信号；远程桥接依赖它断开浏览器连接，避免旧 WebSocket 持有已结束的 session。
- 本地和远程输入会在 `TerminalSession.SendInputAsync` 中按 64 KB 分片，每帧携带 App 生成的请求 ID；Host 只有在 `ConPtySession.Input.WriteAsync` 和 `FlushAsync` 成功后才返回 `InputAck`。不要在 App 写入命名管道后提前确认，否则浏览器会把“Host 尚未写入 ConPTY”误报为成功。等待输入操作和 ack 注册表都有硬上限或超时，终端结束时统一失败并释放。
- `RemoteTerminalBridge` 的状态订阅和统一发送队列各有 512 块硬上限，由单一发送循环串行发送，单次发送超时 30 秒；任一队列写满、发送超时、终端结束、协议版本/序列不合法或远程单条消息超过 16 MB 都会主动断开。每会话最多 8 个状态订阅，全局最多 32 个桥接。WebSocket 接受时设置了 `KeepAliveTimeout`，对端不回 pong 会被自动断开。不要把发送改回每块输出 fire-and-forget 的模式。
- `RemoteAccessService` 从接受 TCP 连接开始就占用一个请求槽：总共 64 个请求槽、10 秒请求头读取超时，WebSocket 升级后继续占槽且另受 32 个桥接上限约束。`MainWindow.Closed` 时先停止监听新连接，再并发关闭所有活跃桥接，并等待请求任务在固定超时内收口，避免慢速请求或大量远程连接造成无界任务和线性退出时间。如果直接杀死进程，浏览器端会在 WebSocket 超时后自动断开。
- `scripts/publish-portable.ps1` 发布后会检查 `TrayTerminal.runtimeconfig.json`，如果产物重新声明了 `Microsoft.AspNetCore.App` 依赖会直接失败。保持框架依赖发布即可，不要改成自包含发布。
- `FileLogger.Write` 会静默吞掉写盘失败（磁盘满、文件被占用），日志失败永远不影响业务逻辑。`App.OnStartup` 注册了 `AppDomain.UnhandledException` 和 `TaskScheduler.UnobservedTaskException` 兜底记录，便于长期挂机后排查崩溃原因。
