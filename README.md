# DeepSeek Harness Desktop

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

The setup is intentionally built locally rather than committed to GitHub:
GitHub blocks large binaries and the offline payload is large.

1. Materialize a clean runtime seed from an already verified desktop runtime.
   This removes pnpm symbolic links so a recipient does not need Windows
   Developer Mode:

```powershell
.\scripts\Materialize-RuntimeSeed.ps1 `
  -SourceRuntime "$env:LOCALAPPDATA\DeepSeekHarnessDesktop\runtime" `
  -OutputRoot C:\build\dsh-runtime
```

`New-RuntimeSeed.ps1` is also available for a clean build directly from the
official npm package when no verified local runtime exists.

2. Build a one-file setup. Supply a Microsoft WebView2 **Fixed Version Runtime**
folder for a fully offline package:

```powershell
.\scripts\Build-OfflineSetup.ps1 `
  -RuntimeSeed C:\build\dsh-runtime `
  -WebView2FixedRuntime C:\build\Microsoft.WebView2.FixedVersionRuntime `
  -DotnetExecutable 'C:\Program Files\dotnet\dotnet.exe' `
  -OutputFile C:\build\DeepSeekHarnessDesktop-Setup.exe
```

Use `-SkipRuntimeLinkValidation` only immediately after a successful
`Materialize-RuntimeSeed.ps1` run; it avoids repeating a long validation scan.

The resulting `Setup.exe` installs to the current user's LocalAppData folder,
creates a desktop shortcut, and starts with no API key. Do not add generated
runtime or setup files to this repository.

## Upstream

This project is a desktop wrapper and does not include DeepSeek Harness source.
Harness is obtained from the official npm package; consult the upstream project
for its license and notices.
