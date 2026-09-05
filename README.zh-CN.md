# DeepSeek Harness Desktop

[English](README.md)

[下载最新 Windows x64 离线安装包](https://github.com/somanybaby/DeepSeekHarnessDesktop/releases/latest)

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

完整安装器在本地构建，通过 GitHub Releases 发布下载；大型 EXE 和生成的运行环境文件不提交到 Git 源码仓库。

1. 重新安装 Windows x64 专用依赖，生成无符号链接、无缓存和无凭据的种子。Node.js 必须包含 npm，pnpm 固定为 11.7.0：

```powershell
.\scripts\New-WindowsRuntimeSeed.ps1 `
  -OutputRoot C:\build\dsh-runtime `
  -NodeSource "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime\node" `
  -PnpmScript "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime\releases\0.1.2-rc.1\node_modules\pnpm\bin\pnpm.cjs" `
  -StoreDirectory C:\build\pnpm-store `
  -HarnessVersion 0.1.2-rc.1
```

请将 `PnpmScript` 改为本机实际 pnpm 11.7.0 的路径。依赖安装使用同一份 Windows 专用策略，保留插件所需运行工具；不再展开旧 `.pnpm` 目录，避免重复副本。构建脚本还会移除依赖内附带的其他平台预编译文件。

2. 使用 Microsoft WebView2 **Fixed Version Runtime** 文件夹构建一个完整离线安装器：

```powershell
.\scripts\Build-OfflineSetup.ps1 `
  -RuntimeSeed C:\build\dsh-runtime `
  -WebView2FixedRuntime C:\build\Microsoft.WebView2.FixedVersionRuntime `
  -DotnetExecutable 'C:\Program Files\dotnet\dotnet.exe' `
  -OutputFile C:\build\DeepSeekHarnessDesktop-Setup.exe
```

构建前会自动检查平台、缓存、凭据和重复依赖。旧 `Materialize-RuntimeSeed.ps1` / `New-RuntimeSeed.ps1` 仅用于历史排查，不再作为发行包构建入口。

生成的安装器会部署到当前用户的 LocalAppData 目录、创建桌面快捷方式，并保证首次 API 配置为空。请不要把生成的运行环境或安装 EXE 提交到本仓库。

### 1.0.1 修复与验证

- 缩短更新暂存路径，并使用无链接的扁平依赖结构，修复 Windows 长路径导致安装脚本启动失败。
- 保留 Harness 0.1.2 的启动认证参数，让健康检查和内嵌界面分别取得会话 Cookie；日志隐藏启动令牌。
- 错误提示优先显示真实错误，不再只显示日志末尾的 Node.js 版本。
- 安装包仍包含 Windows x64 的 WebView2、Node.js 和 .NET；不需要接收方预装这些环境。

回归测试：`dotnet run --project tests/RegressionTests -c Release`。对独立测试运行环境追加 `-- --smoke <runtime>`、`-- --update <runtime>` 或 `-- --rollback <runtime>`；不要对正在使用的安装目录运行更新测试。原生依赖测试：`node scripts/Smoke-WindowsRuntime.mjs <release-directory>`。

### 安装器 1.0.2

- 解压目录先复制为完整候选文件，再替换安装目录，避免直接移动刚解压的 `payload/app` 时因读取占用而失败。
- 短暂文件占用会重试；替换失败会恢复旧文件。已有可用运行环境和用户数据保持不变。
- 清理临时文件失败只记录警告，不再覆盖真正的错误或把安装成功误报为失败；清理结束后才启动软件。
- 安装日志位于 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\setup-logs`。
- 安装事务测试：`dotnet run --project tests/InstallerTests -c Release`，包括实际 Windows 文件占用、回退和清理失败。
- 最终安装 EXE 支持 `--self-test-install <空测试目录>`，用真实内嵌文件验证完整解压、安装和覆盖安装，但不创建桌面快捷方式、不启动界面。第二次对同一测试目录运行可验证覆盖安装。仅用于测试，不要指向正式安装目录。

### 1.0.3 安装进度窗口

- 正常双击安装包会立即显示独立安装窗口，不再只在最后弹出结果。
- 显示当前阶段、总体进度、实际已读取/复制的数据量和耗时。总体百分比按安装阶段计算，不是剩余时间预测。
- 文件处理运行在独立工作线程，窗口可移动、最小化；安装期间不允许直接关闭窗口，以免中断文件替换。
- 完成和失败都会留在窗口里；“查看日志”可打开本次安装日志。
- `--self-test-ui <空测试目录>` 以隐藏窗口执行实际安装，输出初始、安装中、完成画面以及 UI 响应性测试报告，不修改正常安装或用户配置。

GitHub Releases 提供可直接下载的安装 EXE 和 SHA-256 校验文件。源码仓库不存放大型二进制文件。

## 上游项目

本项目是桌面包装器，不包含 DeepSeek Harness 的源代码。Harness 来自官方 npm 包；许可证和公告请参阅上游项目。
