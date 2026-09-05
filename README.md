# DeepSeek Harness Desktop

[中文说明](README.zh-CN.md)

[Download the latest Windows x64 offline installer](https://github.com/somanybaby/DeepSeekHarnessDesktop/releases/latest)

Windows x64 desktop shell for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness).
It runs the official UI in an app window, owns a private local backend, keeps a
single tray-resident instance, exposes API and plugin settings, and provides an
explicit, rollback-safe core update flow.

The one-file offline installer is a self-contained .NET bootstrapper. It does
not use IExpress or CAB files, so the complete runtime can be bundled reliably.

## Privacy and first installation

This repository deliberately contains **no API keys, credentials, chats,
plugins, WebView profile, npm cache, or bundled runtime binaries**.

The offline installer seeds only the application, private Node.js runtime and
Harness package. It never copies or creates `~/.dsh/.credentials.yaml`.
Therefore a first installation on a new Windows user account starts with an
empty API configuration. Existing users keep their `~/.dsh` configuration,
sessions and installed plugins during an application upgrade.

## Runtime behavior

- The backend runs only on a randomly selected loopback port; the app never
  uses the former fixed `127.0.0.1:3080` address.
- The window close button hides the app in the notification area. Tray menu
  **Exit** shuts down both the app and its owned backend process tree.
- The first usable window appears before update checks begin. A newer Harness
  release is downloaded only after the user selects **Update now**.
- The installer can place a fixed WebView2 runtime next to the app, allowing
  fully offline use on computers without the Evergreen WebView2 runtime.

## Build from source

Requirements for the desktop project are the .NET 8 SDK and Windows x64.

```powershell
dotnet build .\DeepSeekHarnessDesktop.csproj -c Release
```

## Build a complete offline setup EXE

Build the installer locally and distribute it through GitHub Releases.
Do not commit the large EXE or generated runtime files to the Git source tree.

1. Create a clean, Windows x64-only runtime with a hoisted, link-free dependency
   tree. Supply a private Node distribution (including npm) and pnpm 11.7.0:

```powershell
.\scripts\New-WindowsRuntimeSeed.ps1 `
  -OutputRoot C:\build\dsh-runtime `
  -NodeSource "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime\node" `
  -PnpmScript "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime\releases\0.1.2-rc.1\node_modules\pnpm\bin\pnpm.cjs" `
  -StoreDirectory C:\build\pnpm-store `
  -HarnessVersion 0.1.2-rc.1
```

Adjust `PnpmScript` to the actual pnpm 11.7.0 path. Updates and builds share the
reviewed Windows install policy. Embedded non-x64 prebuilds are removed, while
plugin installation tools remain available. Do not materialize a `.pnpm` tree
into duplicate package copies.

2. Build a one-file setup. Supply a Microsoft WebView2 **Fixed Version Runtime**
folder for a fully offline package:

```powershell
.\scripts\Build-OfflineSetup.ps1 `
  -RuntimeSeed C:\build\dsh-runtime `
  -WebView2FixedRuntime C:\build\Microsoft.WebView2.FixedVersionRuntime `
  -DotnetExecutable 'C:\Program Files\dotnet\dotnet.exe' `
  -OutputFile C:\build\DeepSeekHarnessDesktop-Setup.exe
```

The builder rejects foreign native binaries, caches, credentials and duplicate
virtual-store packages. The older seed scripts are retained only for historical
diagnostics and are no longer release-build entry points.

The resulting `Setup.exe` installs to the current user's LocalAppData folder,
creates a desktop shortcut, and starts with no API key. Do not add generated
runtime or setup files to this repository.

### 1.0.1 fixes and tests

- Short update staging paths and a hoisted dependency layout avoid Windows cwd limits.
- Harness 0.1.2 startup authentication is preserved for both the health client and
  WebView cookie jars. Launch tokens are redacted from logs.
- Failure messages select the actual error instead of the trailing Node version.
- WebView2, Node.js and .NET are still bundled for offline Windows x64 installation.

Run `dotnet run --project tests/RegressionTests -c Release`. For a disposable
runtime append `-- --smoke <runtime>`, `-- --update <runtime>` or
`-- --rollback <runtime>`; never use a live installation for update tests.
Native-module checks: `node scripts/Smoke-WindowsRuntime.mjs <release-directory>`.

### Installer 1.0.2

The installer now copies extracted files into complete candidates before activation,
retries transient sharing violations, and restores old files after activation failures.
Cleanup failures are warnings rather than installation failures. The application is
launched only after cleanup, with the installed application as its working directory.
Logs are stored under `%LOCALAPPDATA%\DeepSeekHarnessDesktop\setup-logs`.

Run `dotnet run --project tests/InstallerTests -c Release` for actual Windows lock,
rollback, retry and cleanup tests. The final setup EXE also accepts
`--self-test-install <empty-test-directory>` to execute the real extraction and
installation transaction without shortcuts or GUI launch. Run twice on the same
marked test directory to verify upgrades. Never target a live installation.

### 1.0.3 installation progress

Normal launches immediately show a progress window with the current phase, weighted
overall percentage, actual archive/copy byte counts and elapsed time. Installation
runs on a dedicated STA worker; the UI stays responsive and may be minimized.
Completion and errors remain visible, with a button to open the installation log.
Closing is disabled during installation to avoid interrupting file activation.

`--self-test-ui <empty-test-directory>` performs the real installation with a hidden
form, renders initial/progress/completion frames and records UI responsiveness.
It neither launches the desktop application nor changes the normal installation.
GitHub Releases distribute the EXE and SHA-256 checksums separately from the source tree.

## Upstream

This project is a desktop wrapper and does not include DeepSeek Harness source.
Harness is obtained from the official npm package; consult the upstream project
for its license and notices.
