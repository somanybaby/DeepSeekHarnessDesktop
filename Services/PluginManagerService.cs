using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessDesktop.Services;

public sealed record InstalledHarnessPlugin(
    string Name,
    string RequestedVersion,
    bool IsActiveBundle);

public enum PluginOperationKind
{
    Install,
    Remove,
}

public sealed record PluginOperationProgress(
    string Message,
    bool IsCommandOutput = false);

public sealed record PluginOperationResult(
    bool Succeeded,
    string Message,
    int? ExitCode,
    Uri? RuntimeUri,
    IReadOnlyList<string> Output);

/// <summary>
/// The desktop shell owns the Harness process, so plugin mutations use these
/// callbacks instead of starting a second server. NavigateAsync is optional;
/// callers may instead use PluginOperationResult.RuntimeUri.
/// </summary>
public sealed record PluginRuntimeLifecycle(
    Func<CancellationToken, Task> StopAsync,
    Func<CancellationToken, Task<Uri>> RestartAsync,
    Func<Uri, CancellationToken, Task>? NavigateAsync = null);

/// <summary>
/// Manages only the official DSH web-profile plugin command. The caller must
/// provide Node, CLI, working directory and PATH explicitly; this service does
/// not discover or mutate the user's toolchain and never sets DSH_HOME.
/// </summary>
public sealed partial class PluginManagerService
{
    private const int MaximumOutputLines = 400;
    private const int MaximumOutputLineLength = 4_000;

    private readonly string _nodeExecutablePath;
    private readonly string _cliEntryPath;
    private readonly string _workingDirectory;
    private readonly string _processPath;
    private readonly string _webProfileManifestPath;
    private readonly PluginRuntimeLifecycle _lifecycle;
    private readonly SemaphoreSlim _maintenanceGate;
    private readonly object _activeOperationSync = new();

    private CancellationTokenSource? _activeOperationCancellation;
    private TaskCompletionSource? _activeOperationCompletion;

    public PluginManagerService(
        string nodeExecutablePath,
        string cliEntryPath,
        string workingDirectory,
        string processPath,
        PluginRuntimeLifecycle lifecycle,
        SemaphoreSlim? maintenanceGate = null,
        string? webProfileManifestPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliEntryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(lifecycle.StopAsync);
        ArgumentNullException.ThrowIfNull(lifecycle.RestartAsync);

        _nodeExecutablePath = Path.GetFullPath(nodeExecutablePath);
        _cliEntryPath = Path.GetFullPath(cliEntryPath);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _processPath = processPath;
        _lifecycle = lifecycle;
        _maintenanceGate = maintenanceGate ?? new SemaphoreSlim(1, 1);
        _webProfileManifestPath = Path.GetFullPath(webProfileManifestPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh",
            "profiles",
            "web",
            "package.json"));
    }

    public string WebProfileManifestPath => _webProfileManifestPath;

    /// <summary>
    /// True from the moment an operation is queued for the shared maintenance
    /// lock until rollback/commit and runtime recovery have completed.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (_activeOperationSync)
            {
                return _activeOperationCompletion is not null;
            }
        }
    }

    /// <summary>
    /// Requests cancellation of the active CLI process. Recovery and runtime
    /// restart still run to completion with an internal bounded token.
    /// </summary>
    public void CancelActiveOperation()
    {
        lock (_activeOperationSync)
        {
            try
            {
                _activeOperationCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race; the operation is already idle.
            }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task activeOperation;
        lock (_activeOperationSync)
        {
            activeOperation = _activeOperationCompletion?.Task ?? Task.CompletedTask;
        }

        await activeOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAndWaitAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveOperation();
        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstalledHarnessPlugin>> ReadInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_webProfileManifestPath))
        {
            return Array.Empty<InstalledHarnessPlugin>();
        }

        try
        {
            await using var stream = new FileStream(
                _webProfileManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16_384,
                useAsync: true);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            var bundles = ReadBundleNames(root);
            if (!root.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<InstalledHarnessPlugin>();
            }

            var plugins = new List<InstalledHarnessPlugin>();
            foreach (var dependency in dependencies.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = dependency.Value.ValueKind == JsonValueKind.String
                    ? dependency.Value.GetString() ?? "已安装"
                    : "已安装";
                plugins.Add(new InstalledHarnessPlugin(
                    dependency.Name,
                    version,
                    bundles.Contains(dependency.Name)));
            }

            return plugins
                .OrderByDescending(plugin => plugin.IsActiveBundle)
                .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("插件清单暂时无法读取，请稍后重试。", exception);
        }
    }

    public Task<PluginOperationResult> InstallAsync(
        string packageSpec,
        IProgress<PluginOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSpec = NormalizeAndValidateInstallSpec(packageSpec);
        return ExecuteMutationAsync(
            PluginOperationKind.Install,
            normalizedSpec,
            ["add", "--save-exact", normalizedSpec],
            progress,
            cancellationToken);
    }

    public Task<PluginOperationResult> RemoveAsync(
        string packageName,
        IProgress<PluginOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateInstalledPackageName(packageName);
        return ExecuteMutationAsync(
            PluginOperationKind.Remove,
            normalizedName,
            ["remove", normalizedName],
            progress,
            cancellationToken);
    }

    public static string NormalizeAndValidateInstallSpec(string packageSpec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSpec);
        var spec = packageSpec.Trim();
        if (spec.Length >= 2
            && ((spec[0] == '"' && spec[^1] == '"')
                || (spec[0] == '\'' && spec[^1] == '\'')))
        {
            spec = spec[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(spec)
            || spec.Length > 2_048
            || spec[0] == '-'
            || spec.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("插件地址格式不正确。", nameof(packageSpec));
        }

        if (ExactNpmPackageSpecRegex().IsMatch(spec))
        {
            return spec;
        }

        throw new ArgumentException(
            "请输入 npm 包名和精确版本，例如 @scope/plugin@1.2.3。为保护本机安全，不接受 Git、本地路径或压缩包。",
            nameof(packageSpec));
    }

    private async Task<PluginOperationResult> ExecuteMutationAsync(
        PluginOperationKind kind,
        string target,
        IReadOnlyList<string> pluginArguments,
        IProgress<PluginOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operationCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeOperationSync)
        {
            if (_activeOperationCompletion is not null)
            {
                operationCancellation.Dispose();
                throw new InvalidOperationException("已有插件操作正在进行，请等待完成。");
            }

            _activeOperationCancellation = operationCancellation;
            _activeOperationCompletion = operationCompletion;
        }

        var maintenanceLockAcquired = false;
        try
        {
            await _maintenanceGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
            maintenanceLockAcquired = true;
            ValidateToolchain();
            progress?.Report(new PluginOperationProgress("正在暂停后台服务，确保插件文件安全更新……"));
            try
            {
                await _lifecycle.StopAsync(operationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var canceledRuntime = await TryStartRuntimeAsync(progress).ConfigureAwait(false);
                return new PluginOperationResult(
                    false,
                    "操作已取消，后台服务已恢复。",
                    null,
                    canceledRuntime.Uri,
                    Array.Empty<string>());
            }
            catch (Exception exception) when (
                exception is not StackOverflowException
                    and not OutOfMemoryException
                    and not AccessViolationException)
            {
                var failedRuntime = await TryStartRuntimeAsync(progress).ConfigureAwait(false);
                return new PluginOperationResult(
                    false,
                    $"无法安全暂停后台服务：{SanitizeOutput(exception.Message)}",
                    null,
                    failedRuntime.Uri,
                    Array.Empty<string>());
            }

            ProfileMetadataTransaction transaction;
            try
            {
                transaction = ProfileMetadataTransaction.Create(_webProfileManifestPath);
            }
            catch (Exception exception) when (
                exception is not StackOverflowException
                    and not OutOfMemoryException
                    and not AccessViolationException)
            {
                var failedRuntime = await TryStartRuntimeAsync(progress).ConfigureAwait(false);
                return new PluginOperationResult(
                    false,
                    $"无法创建安全备份，插件文件没有更改：{SanitizeOutput(exception.Message)}",
                    null,
                    failedRuntime.Uri,
                    Array.Empty<string>());
            }

            using (transaction)
            {
                CommandResult commandResult;
                try
                {
                    progress?.Report(new PluginOperationProgress(GetRunningMessage(kind, target)));
                    commandResult = await RunOfficialPluginCommandAsync(
                        pluginArguments,
                        progress,
                        operationCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var rollback = await RollbackAndRestartAsync(
                        transaction,
                        progress,
                        Array.Empty<string>()).ConfigureAwait(false);
                    return BuildRollbackResult(
                        "操作已取消。",
                        exitCode: null,
                        rollback);
                }
                catch (Exception exception) when (
                    exception is not StackOverflowException
                        and not OutOfMemoryException
                        and not AccessViolationException)
                {
                    var reason = $"插件操作没有完成：{SanitizeOutput(exception.Message)}";
                    var rollback = await RollbackAndRestartAsync(
                        transaction,
                        progress,
                        Array.Empty<string>()).ConfigureAwait(false);
                    return BuildRollbackResult(reason, exitCode: null, rollback);
                }

                // A killed child can report its exit before WaitForExitAsync
                // observes cancellation. User intent still wins over that
                // platform race, and the transaction must roll back.
                if (operationCancellation.IsCancellationRequested)
                {
                    var rollback = await RollbackAndRestartAsync(
                        transaction,
                        progress,
                        commandResult.Output).ConfigureAwait(false);
                    return BuildRollbackResult(
                        "操作已取消。",
                        commandResult.ExitCode,
                        rollback);
                }

                if (commandResult.ExitCode != 0)
                {
                    var rollback = await RollbackAndRestartAsync(
                        transaction,
                        progress,
                        commandResult.Output).ConfigureAwait(false);
                    return BuildRollbackResult(
                        BuildCommandFailureMessage(commandResult.Output),
                        commandResult.ExitCode,
                        rollback);
                }

                var newRuntime = await TryStartRuntimeAsync(progress).ConfigureAwait(false);
                if (newRuntime.Uri is null)
                {
                    var rollback = await RollbackAndRestartAsync(
                        transaction,
                        progress,
                        commandResult.Output).ConfigureAwait(false);
                    return BuildRollbackResult(
                        "新插件配置无法正常启动。",
                        commandResult.ExitCode,
                        rollback);
                }

                return new PluginOperationResult(
                    true,
                    GetSuccessMessage(kind, target),
                    commandResult.ExitCode,
                    newRuntime.Uri,
                    commandResult.Output);
            }
        }
        finally
        {
            if (maintenanceLockAcquired)
            {
                _maintenanceGate.Release();
            }

            lock (_activeOperationSync)
            {
                _activeOperationCancellation = null;
                _activeOperationCompletion = null;
                operationCompletion.TrySetResult();
            }

            operationCancellation.Dispose();
        }
    }

    private async Task<RuntimeStartResult> TryStartRuntimeAsync(
        IProgress<PluginOperationProgress>? progress)
    {
        progress?.Report(new PluginOperationProgress("正在重新启动 DeepSeek Harness……"));
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var uri = await _lifecycle.RestartAsync(recoveryTimeout.Token).ConfigureAwait(false);
            if (_lifecycle.NavigateAsync is not null)
            {
                try
                {
                    await _lifecycle.NavigateAsync(uri, recoveryTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not StackOverflowException
                        and not OutOfMemoryException
                        and not AccessViolationException)
                {
                    progress?.Report(new PluginOperationProgress(
                        $"后台服务已启动，但页面暂未刷新：{SanitizeOutput(exception.Message)}"));
                }
            }

            progress?.Report(new PluginOperationProgress("后台服务已恢复。"));
            return new RuntimeStartResult(uri, null);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
                and not OutOfMemoryException
                and not AccessViolationException)
        {
            var message = SanitizeOutput(exception.Message);
            progress?.Report(new PluginOperationProgress(
                $"后台服务重启失败：{message}"));
            return new RuntimeStartResult(null, message);
        }
    }

    private async Task<RollbackOutcome> RollbackAndRestartAsync(
        ProfileMetadataTransaction transaction,
        IProgress<PluginOperationProgress>? progress,
        IReadOnlyList<string> originalOutput)
    {
        var recoveryOutput = new List<string>(originalOutput);
        var metadataRestored = false;
        var dependenciesAligned = false;
        progress?.Report(new PluginOperationProgress("正在恢复更新前的插件配置……"));

        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await _lifecycle.StopAsync(recoveryTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
                and not OutOfMemoryException
                and not AccessViolationException)
        {
            recoveryOutput.Add($"停止未完成的后台服务时返回：{SanitizeOutput(exception.Message)}");
        }

        try
        {
            transaction.Restore();
            metadataRestored = true;
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
                and not OutOfMemoryException
                and not AccessViolationException)
        {
            recoveryOutput.Add($"恢复插件清单失败：{SanitizeOutput(exception.Message)}");
        }

        if (metadataRestored)
        {
            try
            {
                progress?.Report(new PluginOperationProgress("正在重新对齐原有插件文件……"));
                var installArguments = transaction.HadLockFile
                    ? new[] { "install", "--frozen-lockfile" }
                    : new[] { "install", "--no-frozen-lockfile" };
                var alignResult = await RunOfficialPluginCommandAsync(
                    installArguments,
                    progress,
                    recoveryTimeout.Token).ConfigureAwait(false);
                recoveryOutput.AddRange(alignResult.Output);
                dependenciesAligned = alignResult.ExitCode == 0;
                if (!dependenciesAligned)
                {
                        recoveryOutput.Add($"原有插件文件对齐返回退出码 {alignResult.ExitCode}。");
                }
                else
                {
                    // pnpm creates a lockfile when an older profile did not
                    // have one. Restore the authoritative metadata once more
                    // so rollback remains byte-for-byte faithful.
                    transaction.Restore();
                }
            }
            catch (Exception exception) when (
                exception is not StackOverflowException
                    and not OutOfMemoryException
                    and not AccessViolationException)
            {
                recoveryOutput.Add($"原有插件文件对齐失败：{SanitizeOutput(exception.Message)}");
            }
        }

        var runtime = await TryStartRuntimeAsync(progress).ConfigureAwait(false);
        var succeeded = metadataRestored && dependenciesAligned && runtime.Uri is not null;
        return new RollbackOutcome(
            succeeded,
            runtime.Uri,
            recoveryOutput,
            runtime.Error);
    }

    private static PluginOperationResult BuildRollbackResult(
        string originalReason,
        int? exitCode,
        RollbackOutcome rollback)
    {
        var message = rollback.Succeeded
            ? $"{originalReason} 已自动恢复更新前的插件配置。"
            : $"{originalReason} 自动恢复未完全完成，请退出桌面端后重新打开。";
        if (!string.IsNullOrWhiteSpace(rollback.RuntimeError))
        {
            message += $" 后台服务：{rollback.RuntimeError}";
        }

        return new PluginOperationResult(
            false,
            message,
            exitCode,
            rollback.RuntimeUri,
            rollback.Output);
    }

    private async Task<CommandResult> RunOfficialPluginCommandAsync(
        IReadOnlyList<string> pluginArguments,
        IProgress<PluginOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _nodeExecutablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (string.Equals(Path.GetExtension(_cliEntryPath), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--import");
            startInfo.ArgumentList.Add("tsx/esm");
        }

        startInfo.ArgumentList.Add(_cliEntryPath);
        startInfo.ArgumentList.Add("plugin");
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add("web");
        foreach (var argument in pluginArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PATH"] = _processPath;
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["FORCE_COLOR"] = "0";
        // Intentionally do not set, clear, or rewrite DSH_HOME. The official
        // command inherits the user's current Harness home and credentials.

        var output = new ConcurrentQueue<string>();
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            CaptureOutput(args.Data, output, progress);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            CaptureOutput(args.Data, output, progress);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动插件管理程序。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The command completed between the cancellation check and kill.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A failed start has no process handle to await.
            }

            throw;
        }

        return new CommandResult(process.ExitCode, output.ToArray());
    }

    private static void CaptureOutput(
        string rawLine,
        ConcurrentQueue<string> output,
        IProgress<PluginOperationProgress>? progress)
    {
        var line = SanitizeOutput(rawLine);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Length > MaximumOutputLineLength)
        {
            line = line[..MaximumOutputLineLength] + "…";
        }

        output.Enqueue(line);
        while (output.Count > MaximumOutputLines && output.TryDequeue(out _))
        {
        }

        progress?.Report(new PluginOperationProgress(line, IsCommandOutput: true));
    }

    private static HashSet<string> ReadBundleNames(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("dsh", out var dsh)
            || dsh.ValueKind != JsonValueKind.Object
            || !dsh.TryGetProperty("profile", out var profile)
            || profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("bundles", out var bundles)
            || bundles.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in bundles.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is { Length: > 0 } name)
            {
                result.Add(name);
            }
        }

        return result;
    }

    private void ValidateToolchain()
    {
        if (!File.Exists(_nodeExecutablePath))
        {
            throw new FileNotFoundException("找不到桌面端自带的运行环境。", _nodeExecutablePath);
        }

        if (!File.Exists(_cliEntryPath))
        {
            throw new FileNotFoundException("找不到 DeepSeek Harness 插件管理入口。", _cliEntryPath);
        }

        if (!Directory.Exists(_workingDirectory))
        {
            throw new DirectoryNotFoundException("找不到 DeepSeek Harness 运行目录。");
        }
    }

    private static string ValidateInstalledPackageName(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        var normalized = packageName.Trim();
        if (!NpmPackageNameRegex().IsMatch(normalized))
        {
            throw new ArgumentException("插件名称无效，请刷新列表后重试。", nameof(packageName));
        }

        return normalized;
    }

    private static string BuildCommandFailureMessage(IReadOnlyList<string> output)
    {
        var usefulLine = output
            .Reverse()
            .FirstOrDefault(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERR_", StringComparison.OrdinalIgnoreCase));
        return usefulLine is null
            ? "插件操作没有完成，请展开下方详情后重试。"
            : $"插件操作没有完成：{usefulLine}";
    }

    private static string GetRunningMessage(PluginOperationKind kind, string target) => kind switch
    {
        PluginOperationKind.Install => $"正在安装 {target}……",
        PluginOperationKind.Remove => $"正在卸载 {target}……",
        _ => "正在处理插件……",
    };

    private static string GetSuccessMessage(PluginOperationKind kind, string target) => kind switch
    {
        PluginOperationKind.Install => $"{target} 已安装并生效。",
        PluginOperationKind.Remove => $"{target} 已卸载。",
        _ => "插件操作已完成。",
    };

    public static string SanitizeOutput(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sanitized = AnsiEscapeRegex().Replace(value, string.Empty);
        sanitized = BearerTokenRegex().Replace(sanitized, "$1[已隐藏]");
        sanitized = NamedSecretRegex().Replace(sanitized, "$1$2[已隐藏]");
        sanitized = CommonTokenRegex().Replace(sanitized, "[已隐藏]");
        sanitized = UrlCredentialRegex().Replace(sanitized, "$1[已隐藏]@");
        sanitized = QuerySecretRegex().Replace(sanitized, "$1[已隐藏]");
        return sanitized.TrimEnd();
    }

    private sealed record CommandResult(int ExitCode, IReadOnlyList<string> Output);

    private sealed record RuntimeStartResult(Uri? Uri, string? Error);

    private sealed record RollbackOutcome(
        bool Succeeded,
        Uri? RuntimeUri,
        IReadOnlyList<string> Output,
        string? RuntimeError);

    /// <summary>
    /// Byte-for-byte snapshot of the profile files that define dependency and
    /// patch state. In particular, .npmrc is never decoded or written to logs.
    /// </summary>
    private sealed class ProfileMetadataTransaction : IDisposable
    {
        private static readonly string[] MetadataFileNames =
        [
            "package.json",
            "pnpm-lock.yaml",
            "pnpm-workspace.yaml",
            "cordis.patch.yml",
            ".npmrc",
        ];

        private readonly string _profileDirectory;
        private readonly string _transactionRoot;
        private readonly string _backupDirectory;
        private readonly Dictionary<string, bool> _originalExistence;
        private int _disposed;

        public bool HadLockFile => _originalExistence["pnpm-lock.yaml"];

        private ProfileMetadataTransaction(
            string profileDirectory,
            string transactionRoot,
            string backupDirectory,
            Dictionary<string, bool> originalExistence)
        {
            _profileDirectory = profileDirectory;
            _transactionRoot = transactionRoot;
            _backupDirectory = backupDirectory;
            _originalExistence = originalExistence;
        }

        public static ProfileMetadataTransaction Create(string manifestPath)
        {
            var canonicalManifest = Path.GetFullPath(manifestPath);
            if (!File.Exists(canonicalManifest))
            {
                throw new FileNotFoundException(
                    "web profile 尚未初始化，请先让 DeepSeek Harness 完成一次启动。",
                    canonicalManifest);
            }

            var profileDirectory = Path.GetDirectoryName(canonicalManifest)
                ?? throw new InvalidOperationException("无法确定 web profile 目录。");
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var transactionRoot = Path.GetFullPath(Path.Combine(
                localData,
                "DeepSeekHarnessDesktop",
                "plugin-transactions"));
            var backupDirectory = Path.Combine(
                transactionRoot,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDirectory);

            var existence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var fileName in MetadataFileNames)
                {
                    var sourcePath = Path.Combine(profileDirectory, fileName);
                    var existed = File.Exists(sourcePath);
                    existence[fileName] = existed;
                    if (existed)
                    {
                        CopyFileAllowingAtomicReaders(
                            sourcePath,
                            Path.Combine(backupDirectory, fileName),
                            createNew: true);
                    }
                }

                return new ProfileMetadataTransaction(
                    profileDirectory,
                    transactionRoot,
                    backupDirectory,
                    existence);
            }
            catch
            {
                TryDeleteTransactionDirectory(transactionRoot, backupDirectory);
                throw;
            }
        }

        public void Restore()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            foreach (var fileName in MetadataFileNames)
            {
                var destinationPath = Path.Combine(_profileDirectory, fileName);
                if (_originalExistence[fileName])
                {
                    var backupPath = Path.Combine(_backupDirectory, fileName);
                    var temporaryPath = destinationPath
                        + ".desktop-rollback-"
                        + Guid.NewGuid().ToString("N");
                    try
                    {
                        CopyFileAllowingAtomicReaders(
                            backupPath,
                            temporaryPath,
                            createNew: true);
                        File.Move(temporaryPath, destinationPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                }
                else if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            TryDeleteTransactionDirectory(_transactionRoot, _backupDirectory);
        }

        private static void CopyFileAllowingAtomicReaders(
            string sourcePath,
            string destinationPath,
            bool createNew)
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var destination = new FileStream(
                destinationPath,
                createNew ? FileMode.CreateNew : FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }

        private static void TryDeleteTransactionDirectory(
            string transactionRoot,
            string backupDirectory)
        {
            try
            {
                var canonicalRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(transactionRoot));
                var canonicalBackup = Path.GetFullPath(backupDirectory);
                var leafName = Path.GetFileName(canonicalBackup);
                var safeLeaf = leafName.Length == 32
                    && leafName.All(character =>
                        character is >= '0' and <= '9'
                            or >= 'a' and <= 'f');
                var isDirectChild = string.Equals(
                    Path.GetDirectoryName(canonicalBackup),
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase);
                if (!safeLeaf || !isDirectChild)
                {
                    return;
                }

                if (Directory.Exists(canonicalBackup))
                {
                    Directory.Delete(canonicalBackup, recursive: true);
                }

                if (Directory.Exists(canonicalRoot)
                    && !Directory.EnumerateFileSystemEntries(canonicalRoot).Any())
                {
                    Directory.Delete(canonicalRoot, recursive: false);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                Debug.WriteLine($"Plugin transaction cleanup failed: {exception.Message}");
            }
        }
    }

    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._~-]*/[a-z0-9][a-z0-9._~-]*|[a-z0-9][a-z0-9._~-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmPackageNameRegex();

    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._~-]*/[a-z0-9][a-z0-9._~-]*|[a-z0-9][a-z0-9._~-]*)@[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactNpmPackageSpecRegex();

    [GeneratedRegex("\\x1B(?:[@-_][0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex("(?i)\\b(Bearer\\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)\\b(api[_-]?key|auth(?:orization)?|token|secret|password|_authToken)(\\s*[:=]\\s*)([^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex("(?i)\\b(?:sk-[A-Za-z0-9_-]{12,}|npm_[A-Za-z0-9]{12,}|gh[pousr]_[A-Za-z0-9]{12,})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommonTokenRegex();

    [GeneratedRegex("(https?://)[^/@\\s]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex("(?i)([?&](?:access_token|api_key|token|key|secret)=)[^&#\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();
}
