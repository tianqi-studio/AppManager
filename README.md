# 应用收纳器（AppManager）

一款基于 **C# + WinUI 3** 的 Windows 应用收纳器与快速启动工具。自动扫描开始菜单和 Microsoft Store 应用，按分类收纳，支持全局快捷键呼出、托盘静默运行和暗色 / 浅色 / 跟随系统主题。

> 本项目使用 WinUI 3（Windows App SDK），不是 WPF。

## 功能特性

- **自动扫描**：读取开始菜单快捷方式、Microsoft Store UWP/MSIX 应用，自动提取名称、路径和图标。
- **分类收纳**：自由创建、重命名、删除分类；一个应用可以同时属于多个分类。
- **快速搜索**：按名称过滤应用，支持启动参数。
- **全局快捷键**：默认 `Ctrl+R`，可在设置中更换，任意界面一键呼出。
- **托盘静默**：启动后驻留系统托盘，不主动弹出窗口；托盘菜单可显示面板、打开设置或退出。
- **三种主题**：暗色、浅色、跟随系统；系统主题变化时自动刷新。
- **原生体验**：WinUI 3 原生控件、贴底弹出动画、点击面板外部自动收起。
- **本地持久化**：分类、应用、窗口尺寸、主题和快捷键全部保存在本机 JSON 中。
- **单实例**：重复启动只会唤醒已有实例。

## 系统要求

- Windows 10 1809（build 17763）或更高版本，x64。
- 发布包已内置 Windows App SDK 运行时，无需单独安装 Windows App Runtime。
- **精简包**还需要安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。
- **自包含包**已内置 .NET 运行时，解压即可运行。

## 下载与运行

从 [Releases](https://github.com/tianqi-studio/AppManager/releases) 下载对应压缩包并解压：

| 包 | 说明 |
| --- | --- |
| `AppManager-*-win-x64.zip` | 精简版，体积更小，需要 .NET 8 Desktop Runtime |
| `AppManager-*-win-x64-selfcontained.zip` | 自包含版，无需安装 .NET 运行时，体积较大 |

双击 `AppManager.exe` 启动。程序默认驻留托盘：

- 按 `Ctrl+R` 或单击托盘图标显示 / 隐藏面板。
- 右键托盘图标可打开设置或退出。
- 启动参数 `--show` 会直接显示面板，`--settings` 会直接打开设置。

## 数据与日志

为了兼容旧版本数据，数据目录仍沿用原来的名称：

- 数据：`%LOCALAPPDATA%\LiquidAppManager\appdata.json`
- 日志：`%LOCALAPPDATA%\LiquidAppManager\logs\app.log`
- 图标缓存：`%LOCALAPPDATA%\LiquidAppManager\icons\`

所有数据仅保存在本机，不会上传。

## 从源码构建

### 环境

- .NET 8 SDK
- Visual Studio 2022（可选，安装 Windows 应用开发 workload）
- Windows 10/11 x64

### 构建

```powershell
dotnet build AppManager.csproj -c Debug -p:Platform=x64 -r win-x64
```

### 发布

```powershell
# 精简版：依赖 .NET 8 Desktop Runtime
dotnet publish AppManager.csproj -c Release -p:Platform=x64 -r win-x64 `
  --self-contained false -o artifacts\publish\win-x64

# 自包含版：内置 .NET 运行时
dotnet publish AppManager.csproj -c Release -p:Platform=x64 -r win-x64 `
  --self-contained true -o artifacts\publish\win-x64-selfcontained
```

## 体积优化说明

Windows App SDK 的 `Microsoft.WindowsAppSDK` 元包会默认带入 Windows ML（ONNX Runtime、DirectML，约 50 MB）、AI 和 Widgets 组件。本项目完全未使用这些 API，因此做了以下处理：

1. 改为直接引用 `Microsoft.WindowsAppSDK.WinUI`、`.Runtime`、`.DWrite` 组件包。
2. 通过 SDK 的 `MicrosoftWindowsAppSDKFilesExcluded` 机制排除 ML / AI / Widgets 负载。
3. 只保留 `zh-CN` 和 `en-us` 语言资源，构建与发布后自动清理其他语言目录。
4. Release 构建不生成 PDB 调试符号。
5. 同时提供精简包和自包含包，兼顾体积与“解压即用”。

优化后，精简版发布目录约 **90 MB**（优化前约 **135 MB**）。

## 项目结构

```text
AppManager/
├─ Assets/                 # 应用图标
├─ Models/                 # 数据模型与主题枚举
├─ Native/                 # Win32 / DWM 互操作
├─ Services/               # 扫描、启动、数据、主题、托盘、热键
├─ Themes/                 # WinUI 3 原生亮暗主题资源
├─ Views/                  # 设置与应用管理页面
├─ App.xaml / App.xaml.cs  # 应用生命周期与单实例
├─ MainWindow.xaml(.cs)    # 主面板、动画与弹层
├─ trim-languages.ps1      # 语言资源裁剪脚本
└─ AppManager.csproj
```

## 许可证

[MIT](LICENSE) © 2026 TQstudio

感谢 OpenCode 在开发过程中的协助。
