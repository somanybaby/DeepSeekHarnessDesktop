using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using DeepSeekHarnessDesktop.Services;

namespace DeepSeekHarnessDesktop;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _applicationStopping = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly string _desktopVersion = ReadDesktopVersion();
    private SingleInstanceCoordinator? _singleInstance;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private HarnessRuntimeManager? _runtime;
    private UpdateService? _updateService;
    private PluginManagerService? _pluginManagerService;
    private PluginManagerWindow? _pluginManagerWindow;
    private Task? _updateOperation;
    private int _exitStarted;

    /// <summary>
    /// Runtime/update modules can register an asynchronous stop handler here.
    /// Handlers run only for a real application exit, never when the window is hidden.
    /// </summary>
    public event Func<CancellationToken, Task>? BeforeExit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimaryInstance)
        {
            await _singleInstance.SignalPrimaryInstanceAsync();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        _singleInstance.ActivationRequested += OnActivationRequested;
        _singleInstance.StartListening();

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.SetVersionInfo(_desktopVersion, "正在启动");

        _trayIcon = new TrayIconService(
            restoreWindow: RestoreMainWindow,
            exitApplication: ExitApplicationAsync);
        _mainWindow.MinimizedToTray += (_, _) => _trayIcon.ShowMinimizedHint();
        _mainWindow.Show();

        try
        {
            var launchOptions = LaunchOptions.Parse(e.Args);
            if (launchOptions.StartUri is not null)
            {
                await _mainWindow.InitializeBrowserAsync(
                    launchOptions.StartUri,
                    _applicationStopping.Token);
                _mainWindow.SetVersionInfo(_desktopVersion, "外部服务");
                _mainWindow.SetUpdateStatus("外部服务测试模式");
                return;
            }

            var portableRuntimeRoot = GetPortableRuntimeRoot();
            var recoveredInterruptedUpdate =
                await UpdateService.RestorePendingPortableUpdateBeforeStartupAsync(
                    portableRuntimeRoot,
                    _applicationStopping.Token);
            if (recoveredInterruptedUpdate)
            {
                Debug.WriteLine("Recovered the last known-good runtime after an interrupted update.");
            }

            _runtime = new HarnessRuntimeManager();
            _runtime.LogReceived += Runtime_OnLogReceived;
            BeforeExit += StopOwnedServicesAsync;

            var startUri = await _runtime.StartAsync(_applicationStopping.Token);
            _mainWindow.SetVersionInfo(_desktopVersion, ReadHarnessVersion(_runtime));
            await _mainWindow.InitializeBrowserAsync(startUri, _applicationStopping.Token);

            _mainWindow.PluginCenterRequested += MainWindow_OnPluginCenterRequested;
            ConfigureUpdateService();
            BeginBackgroundUpdateCheck();
        }
        catch (OperationCanceledException) when (_applicationStopping.IsCancellationRequested)
        {
            // A real exit was requested while the desktop was starting.
        }
        catch (Exception exception)
        {
            _mainWindow.ShowStartupError(
                "DeepSeek Harness 启动失败",
                exception.Message);
            _mainWindow.SetUpdateStatus("后台服务未启动");
        }
    }

    private void ConfigureUpdateService()
    {
        if (_runtime is null || _mainWindow is null)
        {
            return;
        }

        if (!_runtime.IsPackagedRuntime)
        {
            _mainWindow.SetUpdateStatus("本地源码运行模式");
            return;
        }

        var nodeDirectory = Path.GetDirectoryName(_runtime.NodeExecutablePath);
        var runtimeRoot = nodeDirectory is null
            ? null
            : Directory.GetParent(nodeDirectory)?.FullName;
        if (runtimeRoot is null)
        {
            _mainWindow.SetUpdateStatus("未找到便携更新目录");
            return;
        }

        var npmCli = Path.Combine(
            nodeDirectory!,
            "node_modules",
            "npm",
            "bin",
            "npm-cli.js");

        var options = UpdateServiceOptions.CreateForPortableRuntime(
            runtimeRoot,
            _runtime.NodeExecutablePath,
            npmCli,
            stopRuntimeAsync: _runtime.StopAsync,
            startAndVerifyRuntimeAsync: RestartRuntimeAfterUpdateAsync);
        _updateService = new UpdateService(options);
        _updateService.PropertyChanged += UpdateService_OnPropertyChanged;
        _mainWindow.UpdateRequested += MainWindow_OnUpdateRequested;
        RefreshUpdateUi();
    }

    private async Task<bool> RestartRuntimeAfterUpdateAsync(
        string releaseDirectory,
        CancellationToken cancellationToken)
    {
        _ = releaseDirectory;
        if (_runtime is null)
        {
            return false;
        }

        var uri = await _runtime.StartAsync(cancellationToken);
        if (_mainWindow is not null)
        {
            var navigation = await Dispatcher.InvokeAsync(
                () =>
                {
                    ClosePluginCenterAfterRuntimeChange();
                    return _mainWindow.NavigateToAsync(uri, cancellationToken);
                });
            await navigation;
        }

        return true;
    }

    private void BeginBackgroundUpdateCheck()
    {
        if (_updateService is null)
        {
            return;
        }

        _updateOperation = _updateService.CheckAfterStartupAsync(_applicationStopping.Token);
        _ = ObserveUpdateOperationAsync(_updateOperation);
    }

    private async void MainWindow_OnUpdateRequested(object? sender, EventArgs e)
    {
        if (_updateService is null || _updateService.IsBusy)
        {
            return;
        }

        if (_pluginManagerWindow?.IsBusy == true || _pluginManagerService?.IsBusy == true)
        {
            _mainWindow?.ShowUpdateProgress("请先等待插件操作完成", allowRetry: true);
            return;
        }

        ClosePluginCenterAfterRuntimeChange();

        _updateOperation = InstallUpdateWithMaintenanceLockAsync(
            _applicationStopping.Token);
        await ObserveUpdateOperationAsync(_updateOperation);
    }

    private async Task InstallUpdateWithMaintenanceLockAsync(
        CancellationToken cancellationToken)
    {
        if (_updateService is null)
        {
            return;
        }

        await _maintenanceGate.WaitAsync(cancellationToken);
        try
        {
            await _updateService.InstallAvailableUpdateAsync(cancellationToken);
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    private void MainWindow_OnPluginCenterRequested(object? sender, EventArgs e)
    {
        if (_runtime is null || _mainWindow is null)
        {
            _mainWindow?.SetUpdateStatus("插件中心将在后台服务启动后可用");
            return;
        }

        if (_updateService?.IsBusy == true
            || (_updateOperation is not null && !_updateOperation.IsCompleted))
        {
            _mainWindow.SetUpdateStatus("Harness 更新完成后再打开插件中心");
            return;
        }

        if (_pluginManagerWindow is not null)
        {
            if (!_pluginManagerWindow.IsVisible)
            {
                _pluginManagerWindow.Show();
            }

            _pluginManagerWindow.Activate();
            return;
        }

        var nodeDirectory = Path.GetDirectoryName(_runtime.NodeExecutablePath);
        var toolsDirectory = Path.Combine(_runtime.HarnessDirectory, "node_modules", ".bin");
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var processPath = string.Join(
            Path.PathSeparator,
            new[] { nodeDirectory, toolsDirectory, inheritedPath }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        var lifecycle = new PluginRuntimeLifecycle(
            StopAsync: _runtime.StopAsync,
            RestartAsync: _runtime.StartAsync,
            NavigateAsync: NavigateAfterPluginChangeAsync);
        _pluginManagerService = new PluginManagerService(
            _runtime.NodeExecutablePath,
            _runtime.CliEntryPath,
            _runtime.HarnessDirectory,
            processPath,
            lifecycle,
            maintenanceGate: _maintenanceGate);
        _pluginManagerWindow = new PluginManagerWindow(
            _pluginManagerService,
            ReadHarnessVersion(_runtime))
        {
            Owner = _mainWindow,
        };
        _pluginManagerWindow.OperationCompleted += PluginManagerWindow_OnOperationCompleted;
        _pluginManagerWindow.Closed += PluginManagerWindow_OnClosed;
        _pluginManagerWindow.Show();
    }

    private async Task NavigateAfterPluginChangeAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (_mainWindow is null)
        {
            return;
        }

        var navigation = await Dispatcher.InvokeAsync(
            () => _mainWindow.NavigateToAsync(uri, cancellationToken));
        await navigation;
    }

    private void PluginManagerWindow_OnOperationCompleted(
        object? sender,
        PluginOperationResult result)
    {
        _mainWindow?.SetUpdateStatus(result.Message);
    }

    private void PluginManagerWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_pluginManagerWindow is not null)
        {
            _pluginManagerWindow.OperationCompleted -= PluginManagerWindow_OnOperationCompleted;
            _pluginManagerWindow.Closed -= PluginManagerWindow_OnClosed;
        }

        _pluginManagerWindow = null;
        _pluginManagerService = null;
    }

    private void ClosePluginCenterAfterRuntimeChange()
    {
        if (_pluginManagerWindow is null || _pluginManagerWindow.IsBusy)
        {
            return;
        }

        _pluginManagerWindow.Close();
    }

    private async Task ObserveUpdateOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException) when (_applicationStopping.IsCancellationRequested)
        {
            // Normal application exit.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Update operation failed: {exception}");
        }
        finally
        {
            RefreshUpdateUi();
        }
    }

    private void UpdateService_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(RefreshUpdateUi);
    }

    private void RefreshUpdateUi()
    {
        if (_updateService is null || _mainWindow is null)
        {
            return;
        }

        var harnessVersion = string.Equals(
            _updateService.CurrentVersion,
            "未知",
            StringComparison.Ordinal)
            ? _runtime is null ? "未知" : ReadHarnessVersion(_runtime)
            : _updateService.CurrentVersion;
        _mainWindow.SetVersionInfo(_desktopVersion, harnessVersion);

        switch (_updateService.Status)
        {
            case UpdateStatus.Available:
                _mainWindow.ShowUpdateAvailable(_updateService.AvailableVersion);
                break;
            case UpdateStatus.Downloading:
            case UpdateStatus.Applying:
                _mainWindow.ShowUpdateProgress(
                    $"{_updateService.StatusMessage} {_updateService.Progress}%");
                break;
            case UpdateStatus.Error:
                _mainWindow.ShowUpdateProgress(_updateService.StatusMessage, allowRetry: true);
                break;
            default:
                _mainWindow.SetUpdateStatus(_updateService.StatusMessage);
                break;
        }
    }

    private void Runtime_OnLogReceived(object? sender, string line)
    {
        Debug.WriteLine(line);
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(RestoreMainWindow);
    }

    private void RestoreMainWindow()
    {
        _mainWindow?.RestoreFromTray();
    }

    public async Task ExitApplicationAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        _applicationStopping.Cancel();

        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await InvokeBeforeExitHandlersAsync(stopTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Exit must remain available even if a background component does not stop in time.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Application shutdown handler failed: {exception}");
        }
        finally
        {
            if (_pluginManagerWindow is not null)
            {
                _pluginManagerWindow.Close();
                _pluginManagerWindow = null;
                _pluginManagerService = null;
            }

            _trayIcon?.Dispose();
            _trayIcon = null;

            if (_mainWindow is not null)
            {
                _mainWindow.PrepareForApplicationExit();
                _mainWindow.Close();
                _mainWindow = null;
            }

            _singleInstance?.Dispose();
            _singleInstance = null;
            Shutdown(0);
        }
    }

    private async Task StopOwnedServicesAsync(CancellationToken cancellationToken)
    {
        _updateService?.Dispose();
        if (_pluginManagerWindow is not null)
        {
            await _pluginManagerWindow.CancelAndWaitForExitAsync(cancellationToken);
        }
        else if (_pluginManagerService is not null)
        {
            await _pluginManagerService.CancelAndWaitAsync(cancellationToken);
        }

        var updateOperation = _updateOperation;
        if (updateOperation is not null && !updateOperation.IsCompleted)
        {
            try
            {
                await updateOperation.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The outer exit timeout is authoritative.
            }
        }

        if (_runtime is not null)
        {
            await _runtime.StopAsync(cancellationToken);
        }
    }

    private async Task InvokeBeforeExitHandlersAsync(CancellationToken cancellationToken)
    {
        var handlers = BeforeExit;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            await handler(cancellationToken);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _applicationStopping.Cancel();
        _updateService?.Dispose();
        _maintenanceGate.Dispose();
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        if (_runtime is not null)
        {
            _runtime.LogReceived -= Runtime_OnLogReceived;
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _applicationStopping.Dispose();
        base.OnExit(e);
    }

    private static string ReadHarnessVersion(HarnessRuntimeManager runtime)
    {
        var packageJson = runtime.IsPackagedRuntime
            ? Path.Combine(
                runtime.HarnessDirectory,
                "node_modules",
                "@deepseek-ai",
                "dsh",
                "package.json")
            : Path.Combine(runtime.HarnessDirectory, "apps", "cli", "package.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString() ?? "未知";
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to read Harness version: {exception.Message}");
        }

        return "未知";
    }

    private static string ReadDesktopVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static string GetPortableRuntimeRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarnessDesktop",
            "runtime");
    }
}
