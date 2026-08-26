# DeepSeek Harness Desktop

[English](README.md)

这是 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的 Windows x64 桌面外壳。它将官方界面放入独立桌面窗口，并管理仅限本机访问的私有后端，提供 API 配置、插件设置、系统托盘和可回滚的核心更新。

离线安装程序是自包含的 .NET 安装器，不使用 IExpress 或 CAB 文件，因此可以可靠地打包完整运行环境，供另一台 Windows 电脑使用。

## 隐私与首次安装

本仓库**不包含** API Key、凭据、聊天记录、插件、WebView 用户数据、npm 缓存或运行环境二进制文件。

离线安装器只部署桌面程序、私有 Node.js 运行环境和 Harness 包。它不会复制、创建或修改 `%USERPROFILE%\.dsh\.credentials.yaml`：

- 在新电脑或新 Windows 用户账户上首次安装时，API 配置默认为空；请在程序底部的“API 配置”中自行添加。
- 升级已有安装时，现有的 `.dsh` 配置、会话和已安装插件会保留。

## 运行方式

- 后端使用随机的本机回环端口 `127.0.0.1`，不再固定使用 `127.0.0.1:3080`。
- 点击窗口关闭按钮只会隐藏到右下角托盘；从托盘菜单选择“退出”才会关闭桌面程序及其后台服务。
- 程序窗口可用后才会检查更新；发现新版 Harness 时，用户选择“立即更新”才会下载和安装。
- 安装器会把 WebView2 运行环境随程序放置，因此没有安装 Evergreen WebView2 的电脑也可以离线运行。

## 从源码构建

需要 Windows x64 和 .NET 8 SDK：

```powershell
dotnet build .\DeepSeekHarnessDesktop.csproj -c Release
```

## 构建完整离线安装 EXE

完整安装器体积较大，因此只在本地构建，不提交到 GitHub。

1. 从已验证可用的运行环境生成无符号链接的种子。这样接收方不需要开启 Windows 开发者模式：

```powershell
.\scripts\Materialize-RuntimeSeed.ps1 `
  -SourceRuntime "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime" `
  -OutputRoot C:\build\dsh-runtime
```

如果没有已验证的本地运行环境，可以使用 `New-RuntimeSeed.ps1` 从官方 npm 包生成种子。

2. 使用 Microsoft WebView2 **Fixed Version Runtime** 文件夹构建一个完整离线安装器：

```powershell
.\scripts\Build-OfflineSetup.ps1 `
  -RuntimeSeed C:\build\dsh-runtime `
  -WebView2FixedRuntime C:\build\Microsoft.WebView2.FixedVersionRuntime `
  -DotnetExecutable 'C:\Program Files\dotnet\dotnet.exe' `
  -OutputFile C:\build\DeepSeekHarnessDesktop-Setup.exe
```

仅在刚刚成功运行过 `Materialize-RuntimeSeed.ps1` 后使用 `-SkipRuntimeLinkValidation`；它可以跳过耗时的重复链接检查。

生成的安装器会部署到当前用户的 LocalAppData 目录、创建桌面快捷方式，并保证首次 API 配置为空。请不要把生成的运行环境或安装 EXE 提交到本仓库。

## 上游项目

本项目是桌面包装器，不包含 DeepSeek Harness 的源代码。Harness 来自官方 npm 包；许可证和公告请参阅上游项目。
