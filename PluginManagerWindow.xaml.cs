using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeepSeekHarnessDesktop.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;

namespace DeepSeekHarnessDesktop;

public partial class PluginManagerWindow : Window
{
    public const string CodexPluginName = "@deepseek-ai/dsh-subagent-codex";
    public const string ClaudeCodePluginName = "@deepseek-ai/dsh-subagent-claude-code";

    private readonly PluginManagerService _service;
    private readonly string _recommendedVersion;
    private readonly ObservableCollection<PluginDisplayItem> _installedPlugins = [];
    private readonly Dictionary<string, InstalledHarnessPlugin> _installedByName =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _applicationExitRequested;

    /// <param name="recommendedPluginVersion">
    /// Exact DSH-compatible version, normally the currently running Harness
    /// version (for example 0.1.1-rc.2).
    /// </param>
    public PluginManagerWindow(
        PluginManagerService service,
        string recommendedPluginVersion)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedPluginVersion);

        _service = service;
        _recommendedVersion = recommendedPluginVersion.Trim();
        _ = PluginManagerService.NormalizeAndValidateInstallSpec(
            $"{CodexPluginName}@{_recommendedVersion}");
        _ = PluginManagerService.NormalizeAndValidateInstallSpec(
            $"{ClaudeCodePluginName}@{_recommendedVersion}");

        InitializeComponent();
        InstalledPluginsList.ItemsSource = _installedPlugins;
        Loaded += PluginManagerWindow_Loaded;
    }

    public event EventHandler<PluginOperationResult>? OperationCompleted;

    public bool IsBusy => _isBusy || _service.IsBusy;

    /// <summary>
    /// Tray exit can await this before shutting down the WPF dispatcher. The
    /// service kills a canceled CLI tree, rolls back if necessary, and waits
    /// until the Harness runtime has been restored.
    /// </summary>
    public async Task CancelAndWaitForExitAsync(
        CancellationToken cancellationToken = default)
    {
        _applicationExitRequested = true;
        _operationCancellation?.Cancel();
        _service.CancelActiveOperation();
        await _service.WaitForIdleAsync(cancellationToken).ConfigureAwait(true);
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ApplicationIdle,
            cancellationToken);
    }

    private async void PluginManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshInstalledAsync().ConfigureAwait(true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshInstalledAsync().ConfigureAwait(true);
    }

    private async Task RefreshInstalledAsync()
    {
        if (_isBusy)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取插件列表……";
        try
        {
            var installed = await _service.ReadInstalledAsync().ConfigureAwait(true);
            _installedPlugins.Clear();
            _installedByName.Clear();
            foreach (var plugin in installed)
            {
                _installedByName[plugin.Name] = plugin;
                _installedPlugins.Add(new PluginDisplayItem(
                    plugin.Name,
                    plugin.RequestedVersion,
                    plugin.IsActiveBundle
                        ? "已作为插件启用"
                        : "普通依赖（未作为插件启用）"));
            }

            var bundleCount = installed.Count(plugin => plugin.IsActiveBundle);
            InstalledSummaryText.Text = installed.Count == 0
                ? "没有外部插件"
                : $"{installed.Count} 个外部依赖，其中 {bundleCount} 个已作为插件启用";
            EmptyInstalledPanel.Visibility = installed.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateRecommendedCards();
            StatusText.Text = "插件列表已刷新";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
        {
            InstalledSummaryText.Text = "暂时无法读取插件列表";
            StatusText.Text = PluginManagerService.SanitizeOutput(exception.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void UpdateRecommendedCards()
    {
        UpdateRecommendedCard(
            CodexPluginName,
            CodexActionButton,
            CodexStatusText);
        UpdateRecommendedCard(
            ClaudeCodePluginName,
            ClaudeActionButton,
            ClaudeStatusText);
    }

    private void UpdateRecommendedCard(
        string packageName,
        WpfButton actionButton,
        TextBlock statusText)
    {
        if (!_installedByName.TryGetValue(packageName, out var installed))
        {
            statusText.Text = $"兼容版本 {_recommendedVersion}";
            actionButton.Content = "安装";
            actionButton.IsEnabled = true;
            return;
        }

        var installedVersion = installed.RequestedVersion.Trim();
        var isCurrent = string.Equals(
            installedVersion,
            _recommendedVersion,
            StringComparison.OrdinalIgnoreCase);
        statusText.Text = isCurrent
            ? "已安装当前兼容版本"
            : $"可更新到 {_recommendedVersion}";
        actionButton.Content = isCurrent ? "已是当前版本" : "更新兼容版本";
        actionButton.IsEnabled = !isCurrent;
    }

    private async void CodexActionButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallRecommendedAsync(CodexPluginName, "Codex 子代理").ConfigureAwait(true);
    }

    private async void ClaudeActionButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallRecommendedAsync(ClaudeCodePluginName, "Claude Code 子代理")
            .ConfigureAwait(true);
    }

    private Task InstallRecommendedAsync(string packageName, string displayName)
    {
        var exactSpec = $"{packageName}@{_recommendedVersion}";
        return RunOperationAsync(
            $"安装 {displayName}",
            (progress, cancellationToken) => _service.InstallAsync(
                exactSpec,
                progress,
                cancellationToken));
    }

    private async void CustomInstallButton_Click(object sender, RoutedEventArgs e)
    {
        string spec;
        try
        {
            spec = PluginManagerService.NormalizeAndValidateInstallSpec(CustomSpecTextBox.Text);
        }
        catch (ArgumentException exception)
        {
            WpfMessageBox.Show(
                this,
                exception.Message,
                "无法安装",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            CustomSpecTextBox.Focus();
            return;
        }

        await RunOperationAsync(
            $"安装 {spec}",
            (progress, cancellationToken) => _service.InstallAsync(
                spec,
                progress,
                cancellationToken)).ConfigureAwait(true);
    }

    private async void InstalledRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string packageName })
        {
            return;
        }

        await RunOperationAsync(
            $"卸载 {packageName}",
            (progress, cancellationToken) => _service.RemoveAsync(
                packageName,
                progress,
                cancellationToken)).ConfigureAwait(true);
    }

    private async Task RunOperationAsync(
        string actionDescription,
        Func<IProgress<PluginOperationProgress>, CancellationToken, Task<PluginOperationResult>> operation)
    {
        if (_isBusy)
        {
            return;
        }

        var confirmation = WpfMessageBox.Show(
            this,
            $"即将{actionDescription}。\n\n"
            + "插件会作为受信任代码在本机运行，可能访问文件、网络和当前用户数据。"
            + "请确认你信任这个插件及其发布者。操作期间后台服务会暂停并自动恢复。\n\n"
            + "是否继续？",
            "确认插件操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true);
        OutputTextBox.Clear();
        OutputExpander.Visibility = Visibility.Collapsed;
        var progress = new Progress<PluginOperationProgress>(UpdateProgress);
        PluginOperationResult result;
        try
        {
            result = await operation(progress, _operationCancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
                and not OutOfMemoryException
                and not AccessViolationException)
        {
            result = new PluginOperationResult(
                false,
                $"插件操作没有完成：{PluginManagerService.SanitizeOutput(exception.Message)}",
                null,
                null,
                Array.Empty<string>());
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }

        StatusText.Text = result.Message;
        if (result.Output.Count > 0)
        {
            OutputExpander.Visibility = Visibility.Visible;
        }

        RaiseOperationCompleted(result);
        if (_applicationExitRequested)
        {
            return;
        }

        await RefreshInstalledAsync().ConfigureAwait(true);
        StatusText.Text = result.Message;

        WpfMessageBox.Show(
            this,
            result.Message,
            result.Succeeded ? "插件操作完成" : "插件操作未完成",
            MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void UpdateProgress(PluginOperationProgress progress)
    {
        if (progress.IsCommandOutput)
        {
            OutputExpander.Visibility = Visibility.Visible;
            OutputTextBox.AppendText(progress.Message + Environment.NewLine);
            OutputTextBox.ScrollToEnd();
            return;
        }

        StatusText.Text = progress.Message;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ActionsPanel.IsEnabled = !isBusy;
        RefreshButton.IsEnabled = !isBusy;
        OperationProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = isBusy;
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOperationButton.IsEnabled = false;
        StatusText.Text = "正在取消操作并恢复后台服务……";
        _operationCancellation?.Cancel();
        _service.CancelActiveOperation();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isBusy || _applicationExitRequested)
        {
            return;
        }

        e.Cancel = true;
        WpfMessageBox.Show(
            this,
            "插件操作仍在进行。请等待完成，或先点击“取消”并等待后台服务恢复。",
            "暂时不能关闭",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RaiseOperationCompleted(PluginOperationResult result)
    {
        var handlers = OperationCompleted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PluginOperationResult> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, result);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Plugin operation subscriber failed: {exception}");
            }
        }
    }

    private sealed record PluginDisplayItem(
        string Name,
        string RequestedVersion,
        string StatusText);
}
