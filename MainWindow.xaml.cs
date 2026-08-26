using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessDesktop;

public partial class MainWindow : Window
{
    private Uri? _startUri;
    private bool _allowClose;
    private bool _browserInitialized;

    public MainWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? MinimizedToTray;

    public event EventHandler? BrowserReady;

    /// <summary>
    /// Raised when the user clicks the version bar's "立即更新" button.
    /// The update module owns download, validation, installation, and rollback.
    /// </summary>
    public event EventHandler? UpdateRequested;

    public event EventHandler? PluginCenterRequested;

    public async Task InitializeBrowserAsync(Uri startUri, CancellationToken cancellationToken)
    {
        if (_browserInitialized)
        {
            await NavigateToAsync(startUri, cancellationToken);
            return;
        }

        _startUri = startUri;
        ShowStatus("正在启动 DeepSeek Harness…", "正在准备桌面环境，请稍候。", canRetry: false);

        var userDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek Harness Desktop",
            "WebView2");
        Directory.CreateDirectory(userDataDirectory);

        cancellationToken.ThrowIfCancellationRequested();
        // Offline setup packages can include a fixed WebView2 runtime beside the
        // application. Fall back to the machine-wide Evergreen runtime when a
        // developer build or a smaller online setup does not include it.
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: FindBundledWebView2Runtime(),
            userDataFolder: userDataDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        await Browser.EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();

        Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.IsZoomControlEnabled = true;

        Browser.CoreWebView2.NavigationStarting += Browser_OnNavigationStarting;
        Browser.CoreWebView2.NavigationCompleted += Browser_OnNavigationCompleted;
        Browser.CoreWebView2.NewWindowRequested += Browser_OnNewWindowRequested;
        Browser.CoreWebView2.ProcessFailed += Browser_OnProcessFailed;
        _browserInitialized = true;

        await NavigateToAsync(startUri, cancellationToken);
    }

    private void Browser_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            || !IsTrustedNavigationUri(target))
        {
            e.Cancel = true;
            return;
        }

        if (StatusOverlay.Visibility == Visibility.Visible)
        {
            ShowStatus("正在连接 DeepSeek Harness…", "界面即将准备完成。", canRetry: false);
        }
    }

    private void Browser_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            BrowserReady?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowStatus(
            "暂时无法打开 DeepSeek Harness",
            $"连接失败：{e.WebErrorStatus}。请确认后台服务已启动，然后重新加载。",
            canRetry: true);
    }

    private void Browser_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!e.IsUserInitiated
            || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            return;
        }

        if (IsTrustedNavigationUri(target))
        {
            Browser.CoreWebView2.Navigate(target.AbsoluteUri);
            return;
        }

        if (target.Scheme is "https" or "http")
        {
            try
            {
                Process.Start(new ProcessStartInfo(target.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception exception)
            {
                SetUpdateStatus($"无法打开外部链接：{exception.Message}");
            }
        }
    }

    public async Task NavigateToAsync(Uri startUri, CancellationToken cancellationToken = default)
    {
        if (!IsLoopbackHttpUri(startUri))
        {
            throw new ArgumentException("桌面端只能连接本机 DeepSeek Harness 服务。", nameof(startUri));
        }

        _startUri = startUri;
        if (!_browserInitialized || Browser.CoreWebView2 is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Browser.CoreWebView2.CookieManager.DeleteAllCookies();
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Network.clearBrowserCache",
            "{}");
        cancellationToken.ThrowIfCancellationRequested();
        Browser.CoreWebView2.Navigate(startUri.AbsoluteUri);
    }

    private void Browser_OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ShowStatus(
            "桌面界面意外停止",
            $"WebView2 进程状态：{e.ProcessFailedKind}。可以重新加载界面，后台任务不会因此退出。",
            canRetry: true);
    }

    private static string? FindBundledWebView2Runtime()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "webview2");
        return File.Exists(Path.Combine(candidate, "msedgewebview2.exe"))
            ? candidate
            : null;
    }

    public void ShowStartupError(string title, string details)
    {
        ShowStatus(title, details, canRetry: true);
    }

    public void SetVersionInfo(string desktopVersion, string harnessVersion)
    {
        RunOnUiThread(() =>
        {
            VersionText.Text = $"桌面端 {desktopVersion}  ·  Harness {harnessVersion}";
        });
    }

    public void SetUpdateStatus(string status)
    {
        RunOnUiThread(() =>
        {
            UpdateStatusText.Text = status;
            UpdateStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(107, 114, 128));
            UpdateButton.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
        });
    }

    public void ShowUpdateAvailable(string newVersion)
    {
        RunOnUiThread(() =>
        {
            UpdateStatusText.Text = $"发现新版本 {newVersion}";
            UpdateStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(3, 105, 161));
            UpdateButton.Content = "立即更新";
            UpdateButton.IsEnabled = true;
            UpdateButton.Visibility = Visibility.Visible;
        });
    }

    public void ShowUpdateProgress(string status, bool allowRetry = false)
    {
        RunOnUiThread(() =>
        {
            UpdateStatusText.Text = status;
            UpdateButton.Content = allowRetry ? "重试更新" : "正在更新…";
            UpdateButton.IsEnabled = allowRetry;
            UpdateButton.Visibility = Visibility.Visible;
        });
    }

    private void ShowStatus(string title, string details, bool canRetry)
    {
        StatusTitle.Text = title;
        StatusDetails.Text = details;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void ApiConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        await OpenSettingsSectionAsync(
            new[] { "模型", "Models" },
            "API 配置入口暂未加载完成，请稍后重试。");
    }

    private async void PluginsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await OpenSettingsSectionAsync(
            new[] { "插件", "Plugins" },
            "插件页面暂未加载完成，请稍后重试。");
    }

    private void PluginCenterButton_OnClick(object sender, RoutedEventArgs e)
    {
        PluginCenterRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenSettingsSectionAsync(
        IReadOnlyCollection<string> labels,
        string unavailableMessage)
    {
        if (!_browserInitialized || Browser.CoreWebView2 is null)
        {
            SetUpdateStatus(unavailableMessage);
            return;
        }

        var labelsJson = JsonSerializer.Serialize(labels);
        var script = $$"""
            (() => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const labels = new Set({{labelsJson}});
              const triggerLabels = new Set(['设置', 'Settings']);
              const triggers = Array.from(document.querySelectorAll('button[aria-haspopup="dialog"]'));
              const isInSidebar = button => {
                for (let current = button.parentElement; current; current = current.parentElement) {
                  const rect = current.getBoundingClientRect();
                  if (rect.left <= 2 && rect.top <= 2
                      && rect.height >= window.innerHeight * 0.8
                      && rect.width > 0 && rect.width < window.innerWidth * 0.45) return true;
                }
                return false;
              };
              const trigger = triggers.find(button => triggerLabels.has(normalize(button.textContent)))
                || triggers.filter(isInSidebar)
                  .sort((left, right) => right.getBoundingClientRect().bottom
                    - left.getBoundingClientRect().bottom)[0];
              if (!trigger) return false;
              trigger.click();
              const deadline = Date.now() + 2000;
              const selectSection = () => {
                const dialog = document.querySelector('[role="dialog"][aria-modal="true"]');
                const target = dialog && Array.from(dialog.querySelectorAll('button'))
                  .find(button => labels.has(normalize(button.textContent)));
                if (target) {
                  target.click();
                  return;
                }
                if (Date.now() < deadline) window.setTimeout(selectSection, 50);
              };
              selectSection();
              return true;
            })()
            """;

        try
        {
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(script);
            if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                SetUpdateStatus(unavailableMessage);
            }
        }
        catch (Exception exception)
        {
            SetUpdateStatus($"{unavailableMessage} {exception.Message}");
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = Dispatcher.InvokeAsync(action);
        }
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        ShowStatus("正在重新加载…", "正在连接后台服务。", canRetry: false);

        try
        {
            if (_browserInitialized && Browser.CoreWebView2 is not null)
            {
                await NavigateToAsync(
                    _startUri ?? new Uri("http://127.0.0.1:3080/"),
                    CancellationToken.None);
            }
            else if (_startUri is not null)
            {
                await InitializeBrowserAsync(_startUri, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            ShowStartupError("重新加载失败", exception.Message);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void PrepareForApplicationExit()
    {
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            MinimizedToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_browserInitialized && Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.NavigationStarting -= Browser_OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted -= Browser_OnNavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested -= Browser_OnNewWindowRequested;
            Browser.CoreWebView2.ProcessFailed -= Browser_OnProcessFailed;
        }

        Browser.Dispose();
        base.OnClosing(e);
    }

    private bool IsTrustedNavigationUri(Uri uri)
    {
        if (_startUri is null || !IsLoopbackHttpUri(uri))
        {
            return false;
        }

        return string.Equals(uri.Host, _startUri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == _startUri.Port;
    }

    private static bool IsLoopbackHttpUri(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));
    }
}
