using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessDesktop.Services;

/// <summary>
/// Small, UI-friendly state machine used by the desktop shell.
/// The deliberately short set of states keeps XAML triggers stable while
/// <see cref="StatusMessage"/> provides the detail for each update phase.
/// </summary>
public enum UpdateStatus
{
    Idle,
    Checking,
    Available,
    Downloading,
    Applying,
    Ready,
    Error,
}

public enum UpdateInstallationMode
{
    /// <summary>
    /// Preferred distributable layout: an embedded Node/npm runtime plus immutable
    /// versioned npm releases selected by an atomic current.json pointer.
    /// </summary>
    PortableNpm,

    /// <summary>Migration mode for the existing local Git source installation.</summary>
    ManagedGitSource,
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string AvailableVersion,
    bool IsUpdateAvailable);

public sealed record UpdateInstallResult(
    bool Succeeded,
    bool RolledBack,
    string CurrentVersion,
    string? RollbackTag,
    string? Error);

/// <summary>
/// Configuration for updating an existing, managed DeepSeek Harness Git checkout.
/// User configuration is intentionally absent: the updater never reads or writes DSH_HOME.
/// </summary>
public sealed class UpdateServiceOptions
{
    public UpdateInstallationMode InstallationMode { get; init; } =
        UpdateInstallationMode.ManagedGitSource;

    public string SourceDirectory { get; init; } = "";

    /// <summary>
    /// Root of the portable layout. Expected children are node, releases, .staging,
    /// and the active-release pointer current.json.
    /// </summary>
    public string PortableRuntimeRoot { get; init; } = "";

    public required string NodeExecutablePath { get; init; }

    public string PnpmScriptPath { get; init; } = "";

    public string PnpmStoreDirectory { get; init; } = "";

    /// <summary>npm CLI JS file shipped with the portable Node distribution.</summary>
    public string NpmScriptPath { get; init; } = "";

    public required string StagingDirectory { get; init; }

    public string GitExecutablePath { get; init; } = "git";

    public Uri RegistryLatestUri { get; init; } =
        new("https://registry.npmjs.org/%40deepseek-ai%2Fdsh/latest");

    public string OfficialRepositoryUrl { get; init; } =
        "https://github.com/deepseek-ai/deepseek-harness.git";

    public TimeSpan StartupCheckDelay { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Stops only the Harness process owned by this desktop application.</summary>
    public Func<CancellationToken, Task>? StopRuntimeAsync { get; init; }

    /// <summary>
    /// Starts Harness from the supplied source directory and returns only after its
    /// health endpoint has been verified. Returning false triggers an automatic rollback.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? StartAndVerifyRuntimeAsync { get; init; }

    /// <summary>Optional product-specific staged-build health check.</summary>
    public Func<string, CancellationToken, Task<bool>>? ValidateStagedSourceAsync { get; init; }

    public static UpdateServiceOptions CreateForExistingSource(
        string sourceDirectory,
        string nodeExecutablePath,
        string pnpmScriptPath,
        string pnpmStoreDirectory,
        Func<CancellationToken, Task>? stopRuntimeAsync = null,
        Func<string, CancellationToken, Task<bool>>? startAndVerifyRuntimeAsync = null)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var parent = Directory.GetParent(source)?.FullName
            ?? throw new ArgumentException("Harness 源码目录必须有父目录。", nameof(sourceDirectory));

        return new UpdateServiceOptions
        {
            InstallationMode = UpdateInstallationMode.ManagedGitSource,
            SourceDirectory = source,
            NodeExecutablePath = Path.GetFullPath(nodeExecutablePath),
            PnpmScriptPath = Path.GetFullPath(pnpmScriptPath),
            PnpmStoreDirectory = Path.GetFullPath(pnpmStoreDirectory),
            StagingDirectory = Path.Combine(parent, ".dsh-desktop-updates", "staging"),
            StopRuntimeAsync = stopRuntimeAsync,
            StartAndVerifyRuntimeAsync = startAndVerifyRuntimeAsync,
        };
    }

    public static UpdateServiceOptions CreateForPortableRuntime(
        string portableRuntimeRoot,
        string nodeExecutablePath,
        string npmScriptPath,
        Func<CancellationToken, Task>? stopRuntimeAsync = null,
        Func<string, CancellationToken, Task<bool>>? startAndVerifyRuntimeAsync = null,
        string? pnpmScriptPath = null)
    {
        var runtimeRoot = Path.GetFullPath(portableRuntimeRoot);
        return new UpdateServiceOptions
        {
            InstallationMode = UpdateInstallationMode.PortableNpm,
            PortableRuntimeRoot = runtimeRoot,
            NodeExecutablePath = Path.GetFullPath(nodeExecutablePath),
            NpmScriptPath = Path.GetFullPath(npmScriptPath),
            PnpmScriptPath = string.IsNullOrWhiteSpace(pnpmScriptPath)
                ? ""
                : Path.GetFullPath(pnpmScriptPath),
            PnpmStoreDirectory = Path.Combine(runtimeRoot, "pnpm-store"),
            StagingDirectory = Path.Combine(runtimeRoot, ".staging"),
            StopRuntimeAsync = stopRuntimeAsync,
            StartAndVerifyRuntimeAsync = startAndVerifyRuntimeAsync,
        };
    }
}

/// <summary>
/// Checks the official npm release after the desktop UI is usable, prepares the
/// exact official Git tag in an isolated directory, and only then fast-forwards
/// the managed checkout. A local rollback tag is retained for every apply attempt.
/// </summary>
public sealed class UpdateService : INotifyPropertyChanged, IDisposable
{
    private const string PackageJsonRelativePath = "apps/cli/package.json";
    private const string BuiltCliRelativePath = "apps/cli/lib/bin.js";
    private const string PortableArchiveFileName = "official-package.tgz";
    private const string PortableArchiveIntegrityFileName = "official-package.tgz.sha512";
    private const string PortableCurrentManifestFileName = "current.json";
    private const string PortablePreviousManifestFileName = "previous.json";
    private const string PortablePendingUpdateFileName = "pending-update.json";
    public const string PortableToolsRelativePath = "node_modules/.bin";
    public const string BundledPnpmVersion = "11.7.0";
    private const string ReviewedPnpmWorkspace = """
        packages:
          - .

        # Deny unreviewed dependency lifecycle scripts. This is intentionally a
        # fixed allow-list copied from the official Harness workspace policy.
        strictDepBuilds: true
        allowBuilds:
          esbuild: true
          node-pty: true
          koffi: true
          '@deepseek-ai/dsh-subprocess-local': true
          '@google/genai': false
          protobufjs: false
          node-addon-require-builtin: false
        """;
    private static readonly Regex SafeVersionPattern = new(
        "^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly UpdateServiceOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SynchronizationContext? _notificationContext;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateLock = new();

    private UpdateStatus _status = UpdateStatus.Idle;
    private string _statusMessage = "等待检查更新";
    private string _currentVersion = "未知";
    private string _availableVersion = "";
    private int _progress;
    private string? _lastError;
    private string? _lastRollbackTag;
    private DateTimeOffset? _lastCheckedAt;
    private PackageMetadata? _availablePackage;
    private bool _disposed;

    public UpdateService(UpdateServiceOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _notificationContext = SynchronizationContext.Current;

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DeepSeekHarnessDesktop", "1.0"));
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateStatus Status
    {
        get { lock (_stateLock) return _status; }
    }

    public string StatusMessage
    {
        get { lock (_stateLock) return _statusMessage; }
    }

    public string CurrentVersion
    {
        get { lock (_stateLock) return _currentVersion; }
    }

    public string AvailableVersion
    {
        get { lock (_stateLock) return _availableVersion; }
    }

    public int Progress
    {
        get { lock (_stateLock) return _progress; }
    }

    public string? LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public string? LastRollbackTag
    {
        get { lock (_stateLock) return _lastRollbackTag; }
    }

    public DateTimeOffset? LastCheckedAt
    {
        get { lock (_stateLock) return _lastCheckedAt; }
    }

    public bool IsBusy => Status is UpdateStatus.Checking
        or UpdateStatus.Downloading
        or UpdateStatus.Applying;

    public bool IsUpdateAvailable
    {
        get { lock (_stateLock) return _availablePackage is not null; }
    }

    public bool CanCheckForUpdates => !IsBusy;

    public bool CanInstallUpdate => IsUpdateAvailable && !IsBusy;

    /// <summary>
    /// Restores the last known-good portable pointer when an update transaction
    /// was interrupted after pending-update.json was made durable. Call this
    /// before starting the Harness runtime. The operation is idempotent.
    /// </summary>
    public static async Task<bool> RestorePendingPortableUpdateBeforeStartupAsync(
        string portableRuntimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRuntimeRoot);
        var runtimeRoot = Path.GetFullPath(portableRuntimeRoot);
        var pendingPath = Path.Combine(runtimeRoot, PortablePendingUpdateFileName);
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        var pending = await ReadJsonFileAsync<PortablePendingUpdate>(
            pendingPath,
            cancellationToken).ConfigureAwait(false);
        ValidatePendingUpdate(pending);

        var previousPath = Path.Combine(runtimeRoot, PortablePreviousManifestFileName);
        if (!File.Exists(previousPath))
        {
            throw new InvalidDataException(
                "检测到未完成更新，但 runtime/previous.json 不存在，已拒绝猜测回退版本。");
        }

        // Validate the complete old pointer before publishing it. A legacy release
        // is allowed to lack pnpm: that must not prevent recovery or version checks.
        await ReadAndValidatePortableManifestAsync(
            runtimeRoot,
            previousPath,
            requirePnpm: false,
            cancellationToken).ConfigureAwait(false);

        var previousBytes = await File.ReadAllBytesAsync(previousPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteBytesAtomicallyAsync(
            Path.Combine(runtimeRoot, PortableCurrentManifestFileName),
            previousBytes,
            cancellationToken).ConfigureAwait(false);

        // If power is lost before this deletion becomes durable, the next startup
        // safely performs the exact same restore again.
        File.Delete(pendingPath);
        return true;
    }

    /// <summary>
    /// Runtime-aware recovery for callers that construct the updater before the
    /// backend is started. When callbacks are connected it also repairs a backend
    /// process that may have been left partially started by the interrupted update.
    /// </summary>
    public async Task<bool> RecoverInterruptedPortableUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_options.InstallationMode != UpdateInstallationMode.PortableNpm)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await RecoverInterruptedPortableUpdateCoreAsync(linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Intended to be fired-and-forgotten after MainWindow has been shown.
    /// It never delays or blocks initial application startup.
    /// </summary>
    public async Task CheckAfterStartupAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);

        try
        {
            await Task.Delay(_options.StartupCheckDelay, linked.Token).ConfigureAwait(false);
            await CheckForUpdatesAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Normal application shutdown; do not turn it into a visible update error.
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);

        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Transition(UpdateStatus.Idle, "更新检查已取消", 0);
            throw;
        }
        catch (Exception exception)
        {
            SetError("检查更新失败", exception);
            return new UpdateCheckResult(CurrentVersion, AvailableVersion, false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Downloads and validates the release, builds it in staging, then applies it.
    /// The current checkout is not changed until the staged build has succeeded.
    /// </summary>
    public async Task<UpdateInstallResult> InstallAvailableUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);

        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var package = GetAvailablePackage();
            if (package is null)
            {
                var check = await CheckCoreAsync(linked.Token).ConfigureAwait(false);
                package = GetAvailablePackage();
                if (!check.IsUpdateAvailable || package is null)
                {
                    return new UpdateInstallResult(
                        true,
                        false,
                        check.CurrentVersion,
                        LastRollbackTag,
                        null);
                }
            }

            EnsureRuntimeCallbacksConfigured();
            if (_options.InstallationMode == UpdateInstallationMode.PortableNpm)
            {
                var portableUpdate = await PreparePortableUpdateAsync(package, linked.Token)
                    .ConfigureAwait(false);
                return await ApplyPortableUpdateAsync(portableUpdate, linked.Token)
                    .ConfigureAwait(false);
            }

            var prepared = await PrepareUpdateAsync(package, linked.Token).ConfigureAwait(false);
            return await ApplyPreparedUpdateAsync(prepared, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Transition(UpdateStatus.Idle, "更新已取消，当前版本未改变", 0);
            throw;
        }
        catch (Exception exception)
        {
            SetError("更新失败，当前版本未改变", exception);
            return new UpdateInstallResult(
                false,
                false,
                CurrentVersion,
                LastRollbackTag,
                exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        Transition(UpdateStatus.Checking, "正在检查官方版本…", 0, clearError: true);

        if (_options.InstallationMode == UpdateInstallationMode.PortableNpm)
        {
            await RecoverInterruptedPortableUpdateCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var current = _options.InstallationMode == UpdateInstallationMode.PortableNpm
            ? await ReadPortableCurrentVersionAsync(cancellationToken).ConfigureAwait(false)
            : await ReadPackageVersionAsync(
                    _options.SourceDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        SetCurrentVersion(current);

        var package = await GetLatestPackageMetadataAsync(cancellationToken).ConfigureAwait(false);
        var updateAvailable = SemanticVersion.Compare(package.Version, current) > 0;

        lock (_stateLock)
        {
            _lastCheckedAt = DateTimeOffset.Now;
            _availablePackage = updateAvailable ? package : null;
            _availableVersion = updateAvailable ? package.Version : current;
        }

        RaiseProperties(
            nameof(LastCheckedAt),
            nameof(AvailableVersion),
            nameof(IsUpdateAvailable),
            nameof(CanInstallUpdate));

        if (updateAvailable)
        {
            Transition(
                UpdateStatus.Available,
                $"发现新版本 {package.Version}",
                0);
        }
        else
        {
            Transition(UpdateStatus.Ready, $"当前已是最新版本 {current}", 100);
        }

        return new UpdateCheckResult(current, package.Version, updateAvailable);
    }

    private async Task<bool> RecoverInterruptedPortableUpdateCoreAsync(
        CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(
            _options.PortableRuntimeRoot,
            PortablePendingUpdateFileName);
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        Transition(UpdateStatus.Applying, "检测到未完成更新，正在恢复上一版本…", 1);
        var recoveryErrors = new List<Exception>();
        if (_options.StopRuntimeAsync is not null)
        {
            try
            {
                // Deliberately unconditional: StopAsync is idempotent and a failed
                // health probe may still have left a child process alive.
                await _options.StopRuntimeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                recoveryErrors.Add(exception);
            }
        }

        try
        {
            await RestorePendingPortableUpdateBeforeStartupAsync(
                _options.PortableRuntimeRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            recoveryErrors.Add(exception);
        }

        if (recoveryErrors.Count == 0 && _options.StartAndVerifyRuntimeAsync is not null)
        {
            try
            {
                var previousRelease = await TryResolvePortableCurrentReleaseAsync(
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("恢复后的便携运行环境没有有效版本指针。");
                if (!await _options.StartAndVerifyRuntimeAsync(
                        previousRelease,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("上一版本已恢复，但后台服务未能重新启动。");
                }
            }
            catch (Exception exception)
            {
                recoveryErrors.Add(exception);
            }
        }

        if (recoveryErrors.Count != 0)
        {
            throw recoveryErrors.Count == 1
                ? recoveryErrors[0]
                : new AggregateException("未完成更新的自动恢复不完整。", recoveryErrors);
        }

        return true;
    }

    private async Task<PreparedPortableUpdate> PreparePortableUpdateAsync(
        PackageMetadata package,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _options.PortableRuntimeRoot;
        var safeVersion = SanitizePathSegment(package.Version);
        var operationDirectory = Path.Combine(
            _options.StagingDirectory,
            $"dsh-{safeVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var stagedRelease = Path.Combine(operationDirectory, "runtime");
        var logDirectory = Path.Combine(operationDirectory, "logs");
        Directory.CreateDirectory(stagedRelease);
        Directory.CreateDirectory(logDirectory);

        Transition(UpdateStatus.Downloading, $"正在下载 {package.Version}…", 5);
        var packageArchive = Path.Combine(stagedRelease, PortableArchiveFileName);
        await DownloadPackageAsync(
            package.TarballUri,
            packageArchive,
            5,
            24,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在校验官方软件包…", 26);
        await VerifyNpmIntegrityAsync(
            packageArchive,
            package.Integrity,
            cancellationToken).ConfigureAwait(false);

        // Keep both the verified tarball and its registry-provided SHA-512 with
        // every immutable release. Reuse validation never trusts package.json alone.
        await File.WriteAllTextAsync(
            Path.Combine(stagedRelease, PortableArchiveIntegrityFileName),
            GetSha512IntegrityEntry(package.Integrity) + Environment.NewLine,
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);

        var hostPackage = new
        {
            name = "deepseek-harness-desktop-runtime",
            version = "1.0.0",
            @private = true,
            description = "Managed runtime for DeepSeek Harness Desktop",
            dependencies = new Dictionary<string, string>
            {
                ["@deepseek-ai/dsh"] = $"file:./{PortableArchiveFileName}",
                ["pnpm"] = BundledPnpmVersion,
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(stagedRelease, "package.json"),
            JsonSerializer.Serialize(hostPackage, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(stagedRelease, "pnpm-workspace.yaml"),
            ReviewedPnpmWorkspace + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在隔离安装新版运行环境…", 38);
        var installerPnpm = await ResolvePortableInstallerPnpmAsync(
            operationDirectory,
            logDirectory,
            cancellationToken).ConfigureAwait(false);
        await RunPortablePnpmAsync(
            installerPnpm,
            stagedRelease,
            [
                "install",
                "--prod",
                "--no-frozen-lockfile",
                "--reporter=append-only",
                "--store-dir", _options.PnpmStoreDirectory,
            ],
            Path.Combine(logDirectory, "pnpm-install"),
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在验证新版运行环境…", 78);
        var packageDirectory = Path.Combine(
            stagedRelease,
            "node_modules",
            "@deepseek-ai",
            "dsh");
        var installedVersion = await ReadJsonVersionAsync(
            Path.Combine(packageDirectory, "package.json"),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(installedVersion, package.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"隔离安装得到 {installedVersion}，预期为 {package.Version}。");
        }

        var entryPoint = Path.Combine(packageDirectory, "lib", "bin.js");
        if (!File.Exists(entryPoint) || new FileInfo(entryPoint).Length == 0)
        {
            throw new InvalidDataException("官方 npm 包没有生成可运行的 dsh 入口。");
        }

        var smoke = await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            [entryPoint, "--version"],
            stagedRelease,
            Path.Combine(logDirectory, "smoke-version"),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(smoke.StandardOutput.Trim(), package.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"新版冒烟测试返回 {smoke.StandardOutput.Trim()}，预期为 {package.Version}。");
        }

        var pnpmEntryPoint = Path.Combine(
            stagedRelease,
            "node_modules",
            "pnpm",
            "bin",
            "pnpm.cjs");
        var pnpmCommand = Path.Combine(
            stagedRelease,
            PortableToolsRelativePath.Replace('/', Path.DirectorySeparatorChar),
            "pnpm.cmd");
        if (!File.Exists(pnpmEntryPoint) || !File.Exists(pnpmCommand))
        {
            throw new InvalidDataException("便携运行环境未生成插件安装所需的 pnpm.cmd。");
        }

        var pnpmSmoke = await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            [pnpmEntryPoint, "--version"],
            stagedRelease,
            Path.Combine(logDirectory, "smoke-pnpm"),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                pnpmSmoke.StandardOutput.Trim(),
                BundledPnpmVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"便携 pnpm 返回 {pnpmSmoke.StandardOutput.Trim()}，预期为 {BundledPnpmVersion}。");
        }

        if (_options.ValidateStagedSourceAsync is not null &&
            !await _options.ValidateStagedSourceAsync(stagedRelease, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("新版隔离运行验证失败。");
        }

        var manifest = new
        {
            package = "@deepseek-ai/dsh",
            version = package.Version,
            npmIntegrity = package.Integrity,
            runtimeRoot,
            createdAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            Path.Combine(operationDirectory, "update-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "新版已通过验证，准备安装…", 88);
        return new PreparedPortableUpdate(
            package,
            operationDirectory,
            stagedRelease,
            logDirectory);
    }

    private async Task<UpdateInstallResult> ApplyPortableUpdateAsync(
        PreparedPortableUpdate prepared,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _options.PortableRuntimeRoot;
        var releasesRoot = Path.Combine(runtimeRoot, "releases");
        Directory.CreateDirectory(releasesRoot);

        var normalRelease = Path.Combine(
            releasesRoot,
            SanitizePathSegment(prepared.Package.Version));
        var finalRelease = normalRelease;
        if (Directory.Exists(normalRelease))
        {
            var canReuse = await ValidateExistingPortableReleaseAsync(
                normalRelease,
                prepared.Package,
                Path.Combine(prepared.LogDirectory, "existing-release-validation"),
                cancellationToken).ConfigureAwait(false);
            if (!canReuse)
            {
                // Never overwrite or silently reuse a damaged immutable release.
                // Keeping it provides forensic evidence; a repair-suffixed release
                // becomes the new transaction target.
                finalRelease = CreateRepairReleasePath(
                    releasesRoot,
                    SanitizePathSegment(prepared.Package.Version));
            }
        }

        if (!Directory.Exists(finalRelease))
        {
            // The staging root is validated to live on the same drive, so this is an
            // atomic directory rename rather than a partial file-by-file copy.
            Directory.Move(prepared.StagedRelease, finalRelease);
            if (!await ValidateExistingPortableReleaseAsync(
                    finalRelease,
                    prepared.Package,
                    Path.Combine(prepared.LogDirectory, "moved-release-validation"),
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("移动后的便携版本未通过完整性复核。");
            }
        }

        var currentManifestPath = Path.Combine(runtimeRoot, PortableCurrentManifestFileName);
        if (!File.Exists(currentManifestPath))
        {
            throw new InvalidDataException("便携更新缺少可回退的 runtime/current.json。");
        }

        var previousManifestBytes = await File.ReadAllBytesAsync(
            currentManifestPath,
            cancellationToken).ConfigureAwait(false);
        var previousRelease = await TryResolvePortableCurrentReleaseAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("当前便携版本无效，无法建立安全回退点。");
        var previousVersion = await ReadInstalledPortableVersionAsync(
            previousRelease,
            cancellationToken).ConfigureAwait(false);
        var rollbackPoint = $"portable-release/{previousVersion}";
        SetRollbackTag(rollbackPoint);

        var relativeRelease = Path.GetRelativePath(runtimeRoot, finalRelease)
            .Replace(Path.DirectorySeparatorChar, '/');
        var activeManifest = new PortableRuntimeManifest(
            1,
            prepared.Package.Version,
            relativeRelease,
            "node_modules/@deepseek-ai/dsh/lib/bin.js",
            PortableToolsRelativePath,
            DateTimeOffset.UtcNow,
            prepared.Package.Integrity);
        var pendingUpdate = new PortablePendingUpdate(
            1,
            previousVersion,
            prepared.Package.Version,
            relativeRelease,
            DateTimeOffset.UtcNow);

        // Publish the rollback point and transaction journal before changing the
        // active pointer. Write-through atomic files make recovery deterministic
        // even if Windows loses power during the switch.
        await WriteBytesAtomicallyAsync(
            Path.Combine(runtimeRoot, PortablePreviousManifestFileName),
            previousManifestBytes,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicallyAsync(
            Path.Combine(runtimeRoot, PortablePendingUpdateFileName),
            pendingUpdate,
            cancellationToken).ConfigureAwait(false);

        try
        {
            Transition(UpdateStatus.Applying, "正在停止后台服务…", 91);
            await _options.StopRuntimeAsync!(cancellationToken).ConfigureAwait(false);

            Transition(UpdateStatus.Applying, "正在启用新版本…", 94);
            await WriteJsonAtomicallyAsync(
                currentManifestPath,
                activeManifest,
                cancellationToken).ConfigureAwait(false);

            Transition(UpdateStatus.Applying, "正在启动并检查新版…", 98);
            if (!await _options.StartAndVerifyRuntimeAsync!(finalRelease, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("新版启动健康检查失败。");
            }

            // The health check is the commit point. previous.json intentionally
            // remains as a manual rollback point; only the pending marker is cleared.
            File.Delete(Path.Combine(runtimeRoot, PortablePendingUpdateFileName));

            lock (_stateLock)
            {
                _currentVersion = prepared.Package.Version;
                _availableVersion = prepared.Package.Version;
                _availablePackage = null;
            }
            RaiseProperties(
                nameof(CurrentVersion),
                nameof(AvailableVersion),
                nameof(IsUpdateAvailable),
                nameof(CanInstallUpdate));
            Transition(
                UpdateStatus.Ready,
                $"已更新到 {prepared.Package.Version}",
                100,
                clearError: true);

            return new UpdateInstallResult(
                true,
                false,
                prepared.Package.Version,
                rollbackPoint,
                null);
        }
        catch (Exception updateException)
        {
            var rollbackErrors = new List<Exception>();
            try
            {
                // Always stop again. A failed health probe can leave a partially
                // started Node process even when the manager believed it was stopped.
                await _options.StopRuntimeAsync!(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(exception);
            }

            var pointerRestored = false;
            try
            {
                await RestorePendingPortableUpdateBeforeStartupAsync(
                    runtimeRoot,
                    CancellationToken.None).ConfigureAwait(false);
                pointerRestored = true;
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(exception);
            }

            if (pointerRestored && rollbackErrors.Count == 0)
            {
                try
                {
                    if (!await _options.StartAndVerifyRuntimeAsync!(
                            previousRelease,
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "旧版指针已恢复，但后台服务未能重新启动。");
                    }
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(exception);
                }
            }

            SetCurrentVersion(previousVersion);
            var rollbackException = rollbackErrors.Count switch
            {
                0 => null,
                1 => rollbackErrors[0],
                _ => new AggregateException("便携版本回退不完整。", rollbackErrors),
            };
            var message = rollbackException is null
                ? $"新版安装失败，已恢复 {previousVersion}。{updateException.Message}"
                : $"新版安装失败；回退未完整完成。更新错误：{updateException.Message} 回退错误：{rollbackException.Message}";
            SetError(
                message,
                rollbackException is null
                    ? updateException
                    : new AggregateException(updateException, rollbackException),
                messageAlreadyDetailed: true);
            return new UpdateInstallResult(
                false,
                rollbackException is null,
                previousVersion,
                rollbackPoint,
                message);
        }
    }

    private async Task<PreparedUpdate> PrepareUpdateAsync(
        PackageMetadata package,
        CancellationToken cancellationToken)
    {
        var preflight = await InspectManagedCheckoutAsync(
            package.Version,
            cancellationToken).ConfigureAwait(false);

        var safeVersion = SanitizePathSegment(package.Version);
        var operationId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var operationDirectory = Path.Combine(
            _options.StagingDirectory,
            $"dsh-{safeVersion}-{operationId}");
        var stagedSource = Path.Combine(operationDirectory, "source");
        Directory.CreateDirectory(operationDirectory);

        var logDirectory = Path.Combine(operationDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        Transition(UpdateStatus.Downloading, $"正在下载 {package.Version}…", 4);
        var packageArchive = Path.Combine(operationDirectory, "official-package.tgz");
        await DownloadPackageAsync(
            package.TarballUri,
            packageArchive,
            4,
            17,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在校验官方软件包…", 18);
        await VerifyNpmIntegrityAsync(
            packageArchive,
            package.Integrity,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在准备隔离的新版源码…", 22);
        var releaseTag = $"dsh-v{package.Version}";
        await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            [
                "clone",
                "--depth", "1",
                "--branch", releaseTag,
                "--single-branch",
                _options.OfficialRepositoryUrl,
                stagedSource,
            ],
            _options.StagingDirectory,
            Path.Combine(logDirectory, "git-clone"),
            cancellationToken).ConfigureAwait(false);

        var stagedVersion = await ReadPackageVersionAsync(
            stagedSource,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stagedVersion, package.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"官方 Git 标签中的版本为 {stagedVersion}，与 npm 发布的 {package.Version} 不一致。");
        }

        var stagedCommit = (await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", stagedSource, "rev-parse", "HEAD"],
            stagedSource,
            null,
            cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(stagedCommit, preflight.TargetCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "隔离构建的 Git 提交与已验证的官方更新目标不一致。");
        }

        Transition(UpdateStatus.Downloading, "正在安装新版依赖…", 38);
        await RunPnpmAsync(
            stagedSource,
            ["install", "--frozen-lockfile", "--store-dir", _options.PnpmStoreDirectory],
            Path.Combine(logDirectory, "pnpm-install"),
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在构建新版…", 62);
        await RunPnpmAsync(
            stagedSource,
            ["run", "build"],
            Path.Combine(logDirectory, "pnpm-build"),
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "正在验证新版…", 82);
        var builtCli = Path.Combine(stagedSource, BuiltCliRelativePath);
        if (!File.Exists(builtCli) || new FileInfo(builtCli).Length == 0)
        {
            throw new InvalidOperationException("新版构建未生成可运行的 Harness CLI。");
        }

        if (_options.ValidateStagedSourceAsync is not null &&
            !await _options.ValidateStagedSourceAsync(stagedSource, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("新版隔离运行验证失败。");
        }

        var manifest = new
        {
            package = "@deepseek-ai/dsh",
            version = package.Version,
            releaseTag,
            npmIntegrity = package.Integrity,
            currentHead = preflight.CurrentHead,
            targetCommit = preflight.TargetCommit,
            createdAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            Path.Combine(operationDirectory, "update-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        Transition(UpdateStatus.Downloading, "新版已通过验证，准备安装…", 88);
        return new PreparedUpdate(
            package,
            operationDirectory,
            stagedSource,
            preflight.CurrentHead,
            preflight.TargetCommit,
            logDirectory);
    }

    private async Task<UpdateInstallResult> ApplyPreparedUpdateAsync(
        PreparedUpdate prepared,
        CancellationToken cancellationToken)
    {
        var stopRuntime = _options.StopRuntimeAsync!;
        var startAndVerifyRuntime = _options.StartAndVerifyRuntimeAsync!;
        var rollbackTag = CreateRollbackTagName(CurrentVersion);
        var checkoutChanged = false;

        await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", _options.SourceDirectory, "tag", rollbackTag, prepared.CurrentHead],
            _options.SourceDirectory,
            Path.Combine(prepared.LogDirectory, "git-rollback-tag"),
            cancellationToken).ConfigureAwait(false);
        SetRollbackTag(rollbackTag);

        try
        {
            Transition(UpdateStatus.Applying, "正在停止后台服务…", 90);
            await stopRuntime(cancellationToken).ConfigureAwait(false);

            // Re-check after staging because the user or another process may have changed
            // the checkout while the isolated build was running.
            await AssertCheckoutStillSafeAsync(
                prepared.CurrentHead,
                cancellationToken).ConfigureAwait(false);

            Transition(UpdateStatus.Applying, "正在切换到新版本…", 92);
            await RunRequiredProcessAsync(
                _options.GitExecutablePath,
                [
                    "-C", _options.SourceDirectory,
                    "merge", "--ff-only", prepared.TargetCommit,
                ],
                _options.SourceDirectory,
                Path.Combine(prepared.LogDirectory, "git-fast-forward"),
                cancellationToken).ConfigureAwait(false);
            checkoutChanged = true;

            Transition(UpdateStatus.Applying, "正在完成新版安装…", 94);
            await RunPnpmAsync(
                _options.SourceDirectory,
                ["install", "--frozen-lockfile", "--store-dir", _options.PnpmStoreDirectory],
                Path.Combine(prepared.LogDirectory, "apply-pnpm-install"),
                cancellationToken).ConfigureAwait(false);
            await RunPnpmAsync(
                _options.SourceDirectory,
                ["run", "build"],
                Path.Combine(prepared.LogDirectory, "apply-pnpm-build"),
                cancellationToken).ConfigureAwait(false);

            var appliedVersion = await ReadPackageVersionAsync(
                _options.SourceDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(appliedVersion, prepared.Package.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"应用后的版本为 {appliedVersion}，预期为 {prepared.Package.Version}。");
            }

            Transition(UpdateStatus.Applying, "正在启动并检查新版…", 98);
            if (!await startAndVerifyRuntime(_options.SourceDirectory, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("新版启动健康检查失败。");
            }

            lock (_stateLock)
            {
                _currentVersion = prepared.Package.Version;
                _availableVersion = prepared.Package.Version;
                _availablePackage = null;
            }
            RaiseProperties(
                nameof(CurrentVersion),
                nameof(AvailableVersion),
                nameof(IsUpdateAvailable),
                nameof(CanInstallUpdate));
            Transition(
                UpdateStatus.Ready,
                $"已更新到 {prepared.Package.Version}",
                100,
                clearError: true);

            return new UpdateInstallResult(
                true,
                false,
                prepared.Package.Version,
                rollbackTag,
                null);
        }
        catch (Exception updateException)
        {
            var rollbackError = await TryRollbackAsync(
                rollbackTag,
                checkoutChanged,
                prepared.LogDirectory,
                CancellationToken.None).ConfigureAwait(false);

            var message = rollbackError is null
                ? $"新版安装失败，已回退到 {CurrentVersion}。{updateException.Message}"
                : $"新版安装失败；源码回退也未完整完成。更新错误：{updateException.Message} 回退错误：{rollbackError.Message}";
            var combined = rollbackError is null
                ? updateException
                : new AggregateException(updateException, rollbackError);
            SetError(message, combined, messageAlreadyDetailed: true);

            return new UpdateInstallResult(
                false,
                rollbackError is null,
                CurrentVersion,
                rollbackTag,
                message);
        }
    }

    private async Task<Exception?> TryRollbackAsync(
        string rollbackTag,
        bool checkoutChanged,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            // Stop again unconditionally: a failed health check may have left a
            // partial child process even when the earlier stop completed.
            if (_options.StopRuntimeAsync is not null)
            {
                await _options.StopRuntimeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (checkoutChanged)
            {
                Transition(UpdateStatus.Applying, "新版异常，正在恢复上一版本…", 96);
                await RunRequiredProcessAsync(
                    _options.GitExecutablePath,
                    ["-C", _options.SourceDirectory, "reset", "--hard", rollbackTag],
                    _options.SourceDirectory,
                    Path.Combine(logDirectory, "rollback-git-reset"),
                    cancellationToken).ConfigureAwait(false);

                await RunPnpmAsync(
                    _options.SourceDirectory,
                    ["install", "--frozen-lockfile", "--store-dir", _options.PnpmStoreDirectory],
                    Path.Combine(logDirectory, "rollback-pnpm-install"),
                    cancellationToken).ConfigureAwait(false);
                await RunPnpmAsync(
                    _options.SourceDirectory,
                    ["run", "build"],
                    Path.Combine(logDirectory, "rollback-pnpm-build"),
                    cancellationToken).ConfigureAwait(false);
            }

            var restoredVersion = await ReadPackageVersionAsync(
                _options.SourceDirectory,
                cancellationToken).ConfigureAwait(false);
            SetCurrentVersion(restoredVersion);

            if (_options.StartAndVerifyRuntimeAsync is not null &&
                !await _options.StartAndVerifyRuntimeAsync(
                        _options.SourceDirectory,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("旧版本已恢复，但后台服务未能重新启动。");
            }

            return null;
        }
        catch (Exception rollbackException)
        {
            return rollbackException;
        }
    }

    private async Task<CheckoutInspection> InspectManagedCheckoutAsync(
        string targetVersion,
        CancellationToken cancellationToken)
    {
        var source = _options.SourceDirectory;
        var inside = await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "rev-parse", "--is-inside-work-tree"],
            source,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(inside.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Harness 安装目录不是有效的 Git 工作区。");
        }

        var status = await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "status", "--porcelain=v1", "--untracked-files=all"],
            source,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new InvalidOperationException(
                "Harness 源码目录存在未保存的改动；为避免覆盖数据，本次更新已停止。");
        }

        var remote = await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "remote", "get-url", "origin"],
            source,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!RepositoryUrlsMatch(remote.StandardOutput.Trim(), _options.OfficialRepositoryUrl))
        {
            throw new InvalidOperationException(
                "Harness 的 origin 不是 deepseek-ai/deepseek-harness 官方仓库，已拒绝自动更新。");
        }

        var currentHead = (await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "rev-parse", "HEAD"],
            source,
            null,
            cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

        var releaseTag = $"dsh-v{targetVersion}";
        var targetRef = $"refs/dsh-desktop/update-{Guid.NewGuid():N}";
        await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            [
                "-C", source,
                "fetch", "--force", "--no-tags", "origin",
                $"refs/tags/{releaseTag}:{targetRef}",
            ],
            source,
            null,
            cancellationToken).ConfigureAwait(false);

        var targetCommit = (await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "rev-parse", $"{targetRef}^{{commit}}"],
            source,
            null,
            cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

        var ancestor = await RunProcessAsync(
            _options.GitExecutablePath,
            ["-C", source, "merge-base", "--is-ancestor", currentHead, targetCommit],
            source,
            null,
            cancellationToken).ConfigureAwait(false);
        if (ancestor.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "当前安装版本无法安全地快进到官方新版，已拒绝自动覆盖。");
        }

        return new CheckoutInspection(currentHead, targetCommit);
    }

    private async Task AssertCheckoutStillSafeAsync(
        string expectedHead,
        CancellationToken cancellationToken)
    {
        var head = (await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", _options.SourceDirectory, "rev-parse", "HEAD"],
            _options.SourceDirectory,
            null,
            cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(head, expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新准备期间源码版本发生变化，已停止应用新版。");
        }

        var status = await RunRequiredProcessAsync(
            _options.GitExecutablePath,
            ["-C", _options.SourceDirectory, "status", "--porcelain=v1", "--untracked-files=all"],
            _options.SourceDirectory,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new InvalidOperationException("更新准备期间源码出现未保存改动，已停止应用新版。");
        }
    }

    private async Task<PackageMetadata> GetLatestPackageMetadataAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.RegistryLatestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = json.RootElement;
        var version = GetRequiredJsonString(root, "version");
        if (!SafeVersionPattern.IsMatch(version))
        {
            throw new InvalidDataException("npm 返回了无效的版本号。");
        }

        var dist = root.GetProperty("dist");
        var tarball = GetRequiredJsonString(dist, "tarball");
        var integrity = GetRequiredJsonString(dist, "integrity");
        if (!Uri.TryCreate(tarball, UriKind.Absolute, out var tarballUri) ||
            tarballUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("npm 发布包下载地址无效或不是 HTTPS。");
        }

        return new PackageMetadata(version, tarballUri, integrity);
    }

    private async Task DownloadPackageAsync(
        Uri uri,
        string destination,
        int progressStart,
        int progressEnd,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            total += read;
            if (length is > 0)
            {
                var ratio = Math.Clamp((double)total / length.Value, 0, 1);
                var progress = progressStart +
                    (int)Math.Round((progressEnd - progressStart) * ratio);
                SetProgress(progress);
            }
        }
    }

    private static async Task VerifyNpmIntegrityAsync(
        string archivePath,
        string integrity,
        CancellationToken cancellationToken)
    {
        var sha512Entry = GetSha512IntegrityEntry(integrity);

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(sha512Entry[7..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("npm SHA-512 完整性校验值格式错误。", exception);
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("下载的软件包未通过 SHA-512 完整性校验。");
        }
    }

    private static string GetSha512IntegrityEntry(string integrity)
    {
        var entry = integrity
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new InvalidDataException(
            "npm 发布信息没有 SHA-512 完整性校验值。");
    }

    private async Task<string> ResolvePortableInstallerPnpmAsync(
        string operationDirectory,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        try
        {
            var currentRelease = await TryResolvePortableCurrentReleaseAsync(cancellationToken)
                .ConfigureAwait(false);
            if (currentRelease is not null)
            {
                candidates.Add(Path.Combine(
                    currentRelease,
                    "node_modules",
                    "pnpm",
                    "bin",
                    "pnpm.cjs"));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A legacy/corrupt current pointer must not make npm the first choice,
            // but it also must not prevent the reviewed bootstrap fallback below.
        }

        if (!string.IsNullOrWhiteSpace(_options.PnpmScriptPath))
        {
            candidates.Add(_options.PnpmScriptPath);
        }

        var nodeDirectory = Path.GetDirectoryName(_options.NodeExecutablePath);
        if (nodeDirectory is not null)
        {
            candidates.Add(Path.Combine(
                nodeDirectory,
                "node_modules",
                "pnpm",
                "bin",
                "pnpm.cjs"));
        }

        var candidateIndex = 0;
        foreach (var candidate in candidates
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = await RunProcessAsync(
                _options.NodeExecutablePath,
                [candidate, "--version"],
                operationDirectory,
                Path.Combine(logDirectory, $"installer-pnpm-probe-{candidateIndex++}"),
                cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode == 0 && IsSupportedInstallerPnpm(probe.StandardOutput.Trim()))
            {
                return candidate;
            }
        }

        // npm is only a bootstrap fallback. It installs one exact, script-disabled
        // pnpm package in an isolated directory; pnpm then performs the actual DSH
        // install under the reviewed allowBuilds policy.
        if (string.IsNullOrWhiteSpace(_options.NpmScriptPath) ||
            !File.Exists(_options.NpmScriptPath))
        {
            throw new FileNotFoundException(
                "当前版本没有可用 pnpm，且内置 npm 不可用，无法安全准备更新。",
                _options.NpmScriptPath);
        }

        var bootstrapDirectory = Path.Combine(operationDirectory, "pnpm-bootstrap");
        Directory.CreateDirectory(bootstrapDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(bootstrapDirectory, "package.json"),
            "{\"name\":\"dsh-desktop-pnpm-bootstrap\",\"private\":true}\n",
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await RunNpmAsync(
            bootstrapDirectory,
            [
                "install",
                $"pnpm@{BundledPnpmVersion}",
                "--ignore-scripts=true",
                "--package-lock=false",
                "--save=false",
                "--audit=false",
                "--fund=false",
                "--registry=https://registry.npmjs.org/",
            ],
            Path.Combine(logDirectory, "npm-bootstrap-pnpm"),
            cancellationToken).ConfigureAwait(false);

        var bootstrappedPnpm = Path.Combine(
            bootstrapDirectory,
            "node_modules",
            "pnpm",
            "bin",
            "pnpm.cjs");
        var smoke = await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            [bootstrappedPnpm, "--version"],
            bootstrapDirectory,
            Path.Combine(logDirectory, "npm-bootstrap-pnpm-smoke"),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                smoke.StandardOutput.Trim(),
                BundledPnpmVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"安全引导得到 pnpm {smoke.StandardOutput.Trim()}，预期为 {BundledPnpmVersion}。");
        }

        return bootstrappedPnpm;
    }

    private static bool IsSupportedInstallerPnpm(string version)
    {
        var majorText = version.Split('.', 2)[0];
        return int.TryParse(
            majorText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var major) && major >= 11;
    }

    private async Task<ProcessResult> RunPortablePnpmAsync(
        string pnpmScriptPath,
        string workingDirectory,
        IReadOnlyList<string> pnpmArguments,
        string logBasePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(pnpmArguments.Count + 2)
        {
            pnpmScriptPath,
        };
        arguments.AddRange(pnpmArguments);
        arguments.Add("--registry=https://registry.npmjs.org/");
        return await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            arguments,
            workingDirectory,
            logBasePath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunPnpmAsync(
        string workingDirectory,
        IReadOnlyList<string> pnpmArguments,
        string logBasePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(pnpmArguments.Count + 1)
        {
            _options.PnpmScriptPath,
        };
        arguments.AddRange(pnpmArguments);
        return await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            arguments,
            workingDirectory,
            logBasePath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunNpmAsync(
        string workingDirectory,
        IReadOnlyList<string> npmArguments,
        string logBasePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(npmArguments.Count + 3)
        {
            _options.NpmScriptPath,
        };
        arguments.AddRange(npmArguments);
        arguments.Add("--cache");
        arguments.Add(Path.Combine(_options.PortableRuntimeRoot, "npm-cache"));
        return await RunRequiredProcessAsync(
            _options.NodeExecutablePath,
            arguments,
            workingDirectory,
            logBasePath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ValidateExistingPortableReleaseAsync(
        string releaseDirectory,
        PackageMetadata package,
        string logBasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var installedVersion = await ReadInstalledPortableVersionAsync(
                releaseDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(installedVersion, package.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"目录中的版本为 {installedVersion}，预期为 {package.Version}。");
            }

            var archivePath = Path.Combine(releaseDirectory, PortableArchiveFileName);
            var integrityPath = Path.Combine(
                releaseDirectory,
                PortableArchiveIntegrityFileName);
            if (!File.Exists(archivePath) || !File.Exists(integrityPath))
            {
                throw new InvalidDataException("版本目录没有保留官方 tgz 或 SHA-512 记录。");
            }

            var recordedIntegrity = (await File.ReadAllTextAsync(
                integrityPath,
                cancellationToken).ConfigureAwait(false)).Trim();
            var expectedIntegrity = GetSha512IntegrityEntry(package.Integrity);
            if (!string.Equals(
                    recordedIntegrity,
                    expectedIntegrity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("版本目录记录的 SHA-512 与 npm 发布信息不一致。");
            }
            await VerifyNpmIntegrityAsync(
                archivePath,
                package.Integrity,
                cancellationToken).ConfigureAwait(false);

            var cliEntryPoint = Path.Combine(
                releaseDirectory,
                "node_modules",
                "@deepseek-ai",
                "dsh",
                "lib",
                "bin.js");
            if (!File.Exists(cliEntryPoint) || new FileInfo(cliEntryPoint).Length == 0)
            {
                throw new InvalidDataException("版本目录缺少可运行的 dsh CLI。");
            }

            var cliSmoke = await RunRequiredProcessAsync(
                _options.NodeExecutablePath,
                [cliEntryPoint, "--version"],
                releaseDirectory,
                logBasePath + "-cli",
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    cliSmoke.StandardOutput.Trim(),
                    package.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("版本目录中的 dsh CLI 版本检查失败。");
            }

            var pnpmEntryPoint = Path.Combine(
                releaseDirectory,
                "node_modules",
                "pnpm",
                "bin",
                "pnpm.cjs");
            var pnpmCommand = Path.Combine(
                releaseDirectory,
                PortableToolsRelativePath.Replace('/', Path.DirectorySeparatorChar),
                "pnpm.cmd");
            if (!File.Exists(pnpmEntryPoint) || !File.Exists(pnpmCommand))
            {
                throw new InvalidDataException("版本目录缺少插件系统所需的 pnpm。");
            }

            var pnpmSmoke = await RunRequiredProcessAsync(
                _options.NodeExecutablePath,
                [pnpmEntryPoint, "--version"],
                releaseDirectory,
                logBasePath + "-pnpm",
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    pnpmSmoke.StandardOutput.Trim(),
                    BundledPnpmVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"版本目录中的 pnpm 为 {pnpmSmoke.StandardOutput.Trim()}，预期为 {BundledPnpmVersion}。");
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logBasePath)!);
            await File.WriteAllTextAsync(
                logBasePath + "-error.txt",
                exception.ToString(),
                Encoding.UTF8,
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private static string CreateRepairReleasePath(string releasesRoot, string safeVersion)
    {
        while (true)
        {
            var candidate = Path.Combine(
                releasesRoot,
                $"{safeVersion}-repair-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<ProcessResult> RunRequiredProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? logBasePath,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            executable,
            arguments,
            workingDirectory,
            logBasePath,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var diagnostic = LastNonEmptyLine(result.StandardError)
                ?? LastNonEmptyLine(result.StandardOutput)
                ?? "没有可用的错误信息";
            throw new InvalidOperationException(
                $"命令执行失败（退出码 {result.ExitCode}）：{diagnostic}");
        }

        return result;
    }

    private async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? logBasePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between cancellation and Kill.
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{Path.GetFileName(executable)} 执行超过 {_options.CommandTimeout.TotalMinutes:0} 分钟。");
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (logBasePath is not null)
        {
            await File.WriteAllTextAsync(
                $"{logBasePath}.stdout.log",
                stdout,
                Encoding.UTF8,
                CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                $"{logBasePath}.stderr.log",
                stderr,
                Encoding.UTF8,
                CancellationToken.None).ConfigureAwait(false);
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadPackageVersionAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(sourceDirectory, PackageJsonRelativePath);
        if (!File.Exists(path))
        {
            path = Path.Combine(sourceDirectory, "package.json");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var version = GetRequiredJsonString(json.RootElement, "version");
        if (!SafeVersionPattern.IsMatch(version))
        {
            throw new InvalidDataException("本地 package.json 的版本号无效。");
        }

        return version;
    }

    private async Task<string> ReadPortableCurrentVersionAsync(
        CancellationToken cancellationToken)
    {
        var release = await TryResolvePortableCurrentReleaseAsync(cancellationToken)
            .ConfigureAwait(false);
        if (release is null)
        {
            throw new InvalidDataException(
                "便携运行环境尚未初始化：缺少有效的 runtime/current.json。");
        }

        return await ReadInstalledPortableVersionAsync(release, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> TryResolvePortableCurrentReleaseAsync(
        CancellationToken cancellationToken)
    {
        var runtimeRoot = Path.GetFullPath(_options.PortableRuntimeRoot);
        var manifestPath = Path.Combine(runtimeRoot, PortableCurrentManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var resolved = await ReadAndValidatePortableManifestAsync(
            runtimeRoot,
            manifestPath,
            requirePnpm: false,
            cancellationToken).ConfigureAwait(false);
        return resolved.ReleaseDirectory;
    }

    private static async Task<ResolvedPortableManifest> ReadAndValidatePortableManifestAsync(
        string runtimeRoot,
        string manifestPath,
        bool requirePnpm,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadJsonFileAsync<PortableRuntimeManifest>(
            manifestPath,
            cancellationToken).ConfigureAwait(false);

        if (manifest.SchemaVersion != 1 ||
            !SafeVersionPattern.IsMatch(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.ReleaseDirectory) ||
            Path.IsPathRooted(manifest.ReleaseDirectory) ||
            string.IsNullOrWhiteSpace(manifest.EntryPoint) ||
            Path.IsPathRooted(manifest.EntryPoint))
        {
            throw new InvalidDataException($"{Path.GetFileName(manifestPath)} 内容无效。");
        }

        var releasesRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "releases"));
        var release = Path.GetFullPath(Path.Combine(
            runtimeRoot,
            manifest.ReleaseDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(release, releasesRoot))
        {
            throw new InvalidDataException("便携版本指针越出了 releases 目录。");
        }

        var entryPoint = Path.GetFullPath(Path.Combine(
            release,
            manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(entryPoint, release) || !File.Exists(entryPoint))
        {
            throw new InvalidDataException("便携版本指针指向的 dsh 入口无效。");
        }

        var toolsRelative = string.IsNullOrWhiteSpace(manifest.ToolsDirectory)
            ? PortableToolsRelativePath
            : manifest.ToolsDirectory;
        if (Path.IsPathRooted(toolsRelative))
        {
            throw new InvalidDataException("便携版本指针的工具目录必须是相对路径。");
        }

        var toolsDirectory = Path.GetFullPath(Path.Combine(
            release,
            toolsRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(toolsDirectory, release))
        {
            throw new InvalidDataException("便携版本指针的工具目录越出了版本目录。");
        }
        if (requirePnpm && !File.Exists(Path.Combine(toolsDirectory, "pnpm.cmd")))
        {
            throw new InvalidDataException("便携运行环境缺少插件系统所需的 pnpm.cmd。");
        }

        var installedVersion = await ReadInstalledPortableVersionAsync(
            release,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(installedVersion, manifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"版本指针声明 {manifest.Version}，安装内容实际为 {installedVersion}。");
        }

        return new ResolvedPortableManifest(manifest, release);
    }

    private static async Task<T> ReadJsonFileAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容为空。");
    }

    private static void ValidatePendingUpdate(PortablePendingUpdate pending)
    {
        if (pending.SchemaVersion != 1 ||
            !SafeVersionPattern.IsMatch(pending.PreviousVersion) ||
            !SafeVersionPattern.IsMatch(pending.TargetVersion) ||
            string.IsNullOrWhiteSpace(pending.TargetReleaseDirectory) ||
            Path.IsPathRooted(pending.TargetReleaseDirectory))
        {
            throw new InvalidDataException("runtime/pending-update.json 内容无效。");
        }
    }

    private static Task<string> ReadInstalledPortableVersionAsync(
        string releaseDirectory,
        CancellationToken cancellationToken) =>
        ReadJsonVersionAsync(
            Path.Combine(
                releaseDirectory,
                "node_modules",
                "@deepseek-ai",
                "dsh",
                "package.json"),
            cancellationToken);

    private static async Task<string> ReadJsonVersionAsync(
        string packageJsonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(packageJsonPath))
        {
            throw new FileNotFoundException("找不到 npm 软件包描述文件。", packageJsonPath);
        }

        await using var stream = new FileStream(
            packageJsonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var version = GetRequiredJsonString(json.RootElement, "version");
        if (!SafeVersionPattern.IsMatch(version))
        {
            throw new InvalidDataException("npm package.json 的版本号无效。");
        }

        return version;
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });
        await WriteBytesAtomicallyAsync(destination, bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteBytesAtomicallyAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("目标文件必须有父目录。", nameof(destination));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string GetRequiredJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"发布信息缺少 {propertyName}。");
        }

        return property.GetString()!;
    }

    private static bool RepositoryUrlsMatch(string actual, string expected) =>
        string.Equals(
            NormalizeRepositoryUrl(actual),
            NormalizeRepositoryUrl(expected),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRepositoryUrl(string url)
    {
        var normalized = url.Trim();
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized[15..];
        }
        else if (normalized.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized[21..];
        }

        return normalized.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized.TrimEnd('/')[..^4]
            : normalized.TrimEnd('/');
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static string CreateRollbackTagName(string version) =>
        $"desktop-rollback/{SanitizeGitRefSegment(version)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    private static string SanitizeGitRefSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-');
        }

        return builder.ToString().Trim('.', '-');
    }

    private static string? LastNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

    private PackageMetadata? GetAvailablePackage()
    {
        lock (_stateLock)
        {
            return _availablePackage;
        }
    }

    private void EnsureRuntimeCallbacksConfigured()
    {
        if (_options.StopRuntimeAsync is null || _options.StartAndVerifyRuntimeAsync is null)
        {
            throw new InvalidOperationException(
                "更新服务尚未连接后台进程管理器，不能安全地应用更新。");
        }
    }

    private void SetCurrentVersion(string version)
    {
        lock (_stateLock)
        {
            _currentVersion = version;
        }
        RaiseProperties(nameof(CurrentVersion));
    }

    private void SetRollbackTag(string tag)
    {
        lock (_stateLock)
        {
            _lastRollbackTag = tag;
        }
        RaiseProperties(nameof(LastRollbackTag));
    }

    private void SetProgress(int progress)
    {
        lock (_stateLock)
        {
            _progress = Math.Clamp(progress, 0, 100);
        }
        RaiseProperties(nameof(Progress));
    }

    private void Transition(
        UpdateStatus status,
        string message,
        int progress,
        bool clearError = false)
    {
        lock (_stateLock)
        {
            _status = status;
            _statusMessage = message;
            _progress = Math.Clamp(progress, 0, 100);
            if (clearError)
            {
                _lastError = null;
            }
        }

        RaiseProperties(
            nameof(Status),
            nameof(StatusMessage),
            nameof(Progress),
            nameof(LastError),
            nameof(IsBusy),
            nameof(CanCheckForUpdates),
            nameof(CanInstallUpdate));
    }

    private void SetError(
        string prefix,
        Exception exception,
        bool messageAlreadyDetailed = false)
    {
        var message = messageAlreadyDetailed ? prefix : $"{prefix}：{exception.Message}";
        lock (_stateLock)
        {
            _status = UpdateStatus.Error;
            _statusMessage = message;
            _lastError = exception.ToString();
            _progress = 0;
        }

        RaiseProperties(
            nameof(Status),
            nameof(StatusMessage),
            nameof(LastError),
            nameof(Progress),
            nameof(IsBusy),
            nameof(CanCheckForUpdates),
            nameof(CanInstallUpdate));
    }

    private void RaiseProperties(params string[] propertyNames)
    {
        void Raise()
        {
            foreach (var propertyName in propertyNames)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        if (_notificationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _notificationContext))
        {
            Raise();
        }
        else
        {
            _notificationContext.Post(_ => Raise(), null);
        }
    }

    private static void ValidateOptions(UpdateServiceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.NodeExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StagingDirectory);
        var staging = Path.GetFullPath(options.StagingDirectory);

        if (options.InstallationMode == UpdateInstallationMode.PortableNpm)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PortableRuntimeRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PnpmStoreDirectory);
            var runtimeRoot = Path.GetFullPath(options.PortableRuntimeRoot);
            if (!IsSameOrDescendant(staging, runtimeRoot))
            {
                throw new ArgumentException(
                    "便携模式的暂存目录必须位于 runtime 根目录内。",
                    nameof(options));
            }

            if (!string.Equals(
                    Path.GetPathRoot(runtimeRoot),
                    Path.GetPathRoot(staging),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "更新暂存目录必须与便携运行环境位于同一磁盘。",
                    nameof(options));
            }

            var store = Path.GetFullPath(options.PnpmStoreDirectory);
            if (!IsSameOrDescendant(store, runtimeRoot))
            {
                throw new ArgumentException(
                    "便携 pnpm 共享存储必须位于 runtime 根目录内。",
                    nameof(options));
            }

            if (!string.IsNullOrWhiteSpace(options.PnpmScriptPath) &&
                !IsSameOrDescendant(Path.GetFullPath(options.PnpmScriptPath), runtimeRoot))
            {
                throw new ArgumentException(
                    "便携 pnpm 必须来自当前 runtime 发布目录。",
                    nameof(options));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PnpmScriptPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PnpmStoreDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.GitExecutablePath);
            var source = Path.GetFullPath(options.SourceDirectory);
            if (IsSameOrDescendant(staging, source))
            {
                throw new ArgumentException(
                    "更新暂存目录不能位于 Harness 源码目录内。",
                    nameof(options));
            }

            if (!string.Equals(
                    Path.GetPathRoot(source),
                    Path.GetPathRoot(staging),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "更新暂存目录必须与 Harness 源码目录位于同一磁盘。",
                    nameof(options));
            }
        }

        if (options.CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "命令超时时间必须大于零。");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        // Do not dispose the gate here: an in-flight operation may still be
        // unwinding its finally block and must be able to Release it safely.
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record PackageMetadata(string Version, Uri TarballUri, string Integrity);

    private sealed record CheckoutInspection(string CurrentHead, string TargetCommit);

    private sealed record PreparedUpdate(
        PackageMetadata Package,
        string OperationDirectory,
        string StagedSource,
        string CurrentHead,
        string TargetCommit,
        string LogDirectory);

    private sealed record PreparedPortableUpdate(
        PackageMetadata Package,
        string OperationDirectory,
        string StagedRelease,
        string LogDirectory);

    private sealed record PortableRuntimeManifest(
        int SchemaVersion,
        string Version,
        string ReleaseDirectory,
        string EntryPoint,
        string ToolsDirectory,
        DateTimeOffset ActivatedAt,
        string? NpmIntegrity = null);

    private sealed record PortablePendingUpdate(
        int SchemaVersion,
        string PreviousVersion,
        string TargetVersion,
        string TargetReleaseDirectory,
        DateTimeOffset CreatedAt);

    private sealed record ResolvedPortableManifest(
        PortableRuntimeManifest Manifest,
        string ReleaseDirectory);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly int[] _core;
        private readonly string[] _preRelease;

        private SemanticVersion(int[] core, string[] preRelease)
        {
            _core = core;
            _preRelease = preRelease;
        }

        public static int Compare(string left, string right) =>
            Parse(left).CompareTo(Parse(right));

        public static SemanticVersion Parse(string value)
        {
            var normalized = value.Trim().TrimStart('v', 'V');
            var buildIndex = normalized.IndexOf('+');
            if (buildIndex >= 0)
            {
                normalized = normalized[..buildIndex];
            }

            var parts = normalized.Split('-', 2);
            var coreParts = parts[0].Split('.');
            if (coreParts.Length is < 1 or > 4)
            {
                throw new FormatException($"无法识别版本号 {value}。");
            }

            var core = new int[Math.Max(3, coreParts.Length)];
            for (var index = 0; index < coreParts.Length; index++)
            {
                if (!int.TryParse(
                        coreParts[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out core[index]) ||
                    core[index] < 0)
                {
                    throw new FormatException($"无法识别版本号 {value}。");
                }
            }

            var preRelease = parts.Length == 2
                ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
                : [];
            return new SemanticVersion(core, preRelease);
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var coreLength = Math.Max(_core.Length, other._core.Length);
            for (var index = 0; index < coreLength; index++)
            {
                var left = index < _core.Length ? _core[index] : 0;
                var right = index < other._core.Length ? other._core[index] : 0;
                var coreComparison = left.CompareTo(right);
                if (coreComparison != 0)
                {
                    return coreComparison;
                }
            }

            if (_preRelease.Length == 0 || other._preRelease.Length == 0)
            {
                return _preRelease.Length == other._preRelease.Length
                    ? 0
                    : _preRelease.Length == 0 ? 1 : -1;
            }

            var preLength = Math.Max(_preRelease.Length, other._preRelease.Length);
            for (var index = 0; index < preLength; index++)
            {
                if (index >= _preRelease.Length)
                {
                    return -1;
                }
                if (index >= other._preRelease.Length)
                {
                    return 1;
                }

                var comparison = ComparePreReleaseIdentifier(
                    _preRelease[index],
                    other._preRelease[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        private static int ComparePreReleaseIdentifier(string left, string right)
        {
            var leftNumeric = int.TryParse(
                left,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var leftNumber);
            var rightNumeric = int.TryParse(
                right,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rightNumber);
            if (leftNumeric && rightNumeric)
            {
                return leftNumber.CompareTo(rightNumber);
            }
            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }
    }
}
