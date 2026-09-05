using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessDesktop;

/// <summary>
/// Locates the checked-out Harness runtime and the private Node.js runtime used
/// by the desktop application. No DSH_HOME value is introduced here: Harness
/// therefore keeps using the user's existing ~/.dsh data and credentials.
/// </summary>
public sealed record HarnessRuntimeOptions(
    string HarnessDirectory,
    string NodeExecutablePath)
{
    /// <summary>
    /// Absolute CLI entry. A null value is inferred as source bin.ts when that
    /// file exists, otherwise as the packaged @deepseek-ai/dsh lib/bin.js.
    /// </summary>
    public string? CliEntryPath { get; init; }

    /// <summary>Null means infer from the CLI entry's .ts extension.</summary>
    public bool? UseTypeScriptLoader { get; init; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan GracefulShutdownTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ForcedShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Finds development or packaged runtime layouts without consulting a
    /// machine-wide Node installation.
    /// </summary>
    public static HarnessRuntimeOptions DiscoverDefault()
    {
        var searchRoots = EnumerateSearchRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Installed/updated npm runtime always wins. This keeps the shipped
        // desktop application independent from a developer workspace checkout.
        var packagedRuntime = FindPackagedRuntime(searchRoots);
        if (packagedRuntime is not null)
        {
            return packagedRuntime;
        }

        var harnessDirectory = FindSourceHarnessDirectory(searchRoots)
            ?? throw new DirectoryNotFoundException(
                "找不到 DeepSeek Harness 便携运行时或源码目录。");

        var nodeExecutable = FindNodeExecutable(searchRoots, harnessDirectory)
            ?? throw new FileNotFoundException("找不到桌面端自带的 Node.js 运行环境（node.exe）。");

        return new HarnessRuntimeOptions(harnessDirectory, nodeExecutable)
        {
            CliEntryPath = Path.Combine(harnessDirectory, "apps", "cli", "src", "bin.ts"),
            UseTypeScriptLoader = true,
        };
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var configuredRuntime = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configuredRuntime))
        {
            yield return Environment.ExpandEnvironmentVariables(configuredRuntime);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localData))
        {
            yield return Path.Combine(localData, "DeepSeekHarnessDesktop");
            yield return Path.Combine(localData, "DeepSeek Harness Desktop");
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            yield return current.FullName;
            yield return Path.Combine(current.FullName, "work");
            yield return Path.Combine(current.FullName, "runtime");
        }
    }

    private static HarnessRuntimeOptions? FindPackagedRuntime(IEnumerable<string> searchRoots)
    {
        foreach (var root in searchRoots)
        {
            var candidates = new[]
            {
                root,
                Path.Combine(root, "current"),
                Path.Combine(root, "runtime"),
                Path.Combine(root, "runtime", "current"),
                Path.Combine(root, "versions", "current"),
            };

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manifestRuntime = TryReadCurrentManifest(candidate);
                if (manifestRuntime is not null)
                {
                    return manifestRuntime;
                }

                var cliEntry = Path.Combine(
                    candidate,
                    "node_modules",
                    "@deepseek-ai",
                    "dsh",
                    "lib",
                    "bin.js");
                if (!File.Exists(cliEntry))
                {
                    continue;
                }

                var nodeExecutable = FindPrivateNodeForRuntime(candidate);
                if (nodeExecutable is null)
                {
                    continue;
                }

                return new HarnessRuntimeOptions(
                    Path.GetFullPath(candidate),
                    Path.GetFullPath(nodeExecutable))
                {
                    CliEntryPath = Path.GetFullPath(cliEntry),
                    UseTypeScriptLoader = false,
                };
            }
        }

        return null;
    }

    private static HarnessRuntimeOptions? TryReadCurrentManifest(string runtimeRoot)
    {
        var manifestPath = Path.Combine(runtimeRoot, "current.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var root = document.RootElement;
            var releaseValue = TryGetManifestString(root, "releaseDirectory");
            var version = TryGetManifestString(root, "version");
            if (string.IsNullOrWhiteSpace(releaseValue))
            {
                if (string.IsNullOrWhiteSpace(version))
                {
                    return null;
                }

                releaseValue = Path.Combine("releases", version);
            }

            var canonicalRuntimeRoot = Path.GetFullPath(runtimeRoot);
            var releaseDirectory = ResolveUnderRoot(canonicalRuntimeRoot, releaseValue);
            if (releaseDirectory is null || !Directory.Exists(releaseDirectory))
            {
                return null;
            }

            var entryValue = TryGetManifestString(root, "entryPoint");
            var entryCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(entryValue))
            {
                var fromRuntimeRoot = ResolveUnderRoot(canonicalRuntimeRoot, entryValue);
                if (fromRuntimeRoot is not null)
                {
                    entryCandidates.Add(fromRuntimeRoot);
                }

                var fromReleaseDirectory = ResolveUnderRoot(releaseDirectory, entryValue);
                if (fromReleaseDirectory is not null)
                {
                    entryCandidates.Add(fromReleaseDirectory);
                }
            }

            entryCandidates.Add(Path.Combine(
                releaseDirectory,
                "node_modules",
                "@deepseek-ai",
                "dsh",
                "lib",
                "bin.js"));

            var cliEntry = entryCandidates
                .Select(Path.GetFullPath)
                .Where(path => IsPathWithin(canonicalRuntimeRoot, path))
                .FirstOrDefault(File.Exists);
            var nodeExecutable = FindPrivateNodeForRuntime(canonicalRuntimeRoot);
            if (cliEntry is null || nodeExecutable is null)
            {
                return null;
            }

            return new HarnessRuntimeOptions(releaseDirectory, Path.GetFullPath(nodeExecutable))
            {
                CliEntryPath = cliEntry,
                UseTypeScriptLoader = false,
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            // An interrupted/invalid update manifest is never executable. The
            // updater can repair it; development discovery continues below.
            return null;
        }
    }

    private static string? TryGetManifestString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? ResolveUnderRoot(string root, string path)
    {
        var resolved = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        return IsPathWithin(root, resolved) ? resolved : null;
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalCandidate = Path.GetFullPath(candidate);
        return string.Equals(canonicalRoot, canonicalCandidate, StringComparison.OrdinalIgnoreCase)
            || canonicalCandidate.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPrivateNodeForRuntime(string runtimeDirectory)
    {
        var parent = Directory.GetParent(runtimeDirectory)?.FullName;
        var directCandidates = new List<string>
        {
            Path.Combine(runtimeDirectory, "node.exe"),
            Path.Combine(runtimeDirectory, "node", "node.exe"),
        };

        if (parent is not null)
        {
            directCandidates.Add(Path.Combine(parent, "node.exe"));
            directCandidates.Add(Path.Combine(parent, "node", "node.exe"));
        }

        var direct = directCandidates.FirstOrDefault(File.Exists);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var directory in new[] { runtimeDirectory, parent }.OfType<string>())
        {
            try
            {
                var versioned = Directory
                    .EnumerateDirectories(directory, "node-v*-win-x64", SearchOption.TopDirectoryOnly)
                    .Select(path => Path.Combine(path, "node.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(GetExecutableVersion)
                    .FirstOrDefault();
                if (versioned is not null)
                {
                    return versioned;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Continue to the next private candidate.
            }
        }

        return null;
    }

    private static string? FindSourceHarnessDirectory(IEnumerable<string> searchRoots)
    {
        foreach (var root in searchRoots)
        {
            var candidates = new[]
            {
                root,
                Path.Combine(root, "deepseek-harness"),
                Path.Combine(root, "runtime", "deepseek-harness"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "apps", "cli", "src", "bin.ts"))
                    && File.Exists(Path.Combine(candidate, "package.json")))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? FindNodeExecutable(
        IEnumerable<string> searchRoots,
        string harnessDirectory)
    {
        var directCandidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "node", "node.exe"),
            Path.Combine(AppContext.BaseDirectory, "node", "node.exe"),
            Path.Combine(Path.GetDirectoryName(harnessDirectory)!, "node", "node.exe"),
        };

        foreach (var root in searchRoots)
        {
            directCandidates.Add(Path.Combine(root, "node", "node.exe"));
            directCandidates.Add(Path.Combine(root, "runtime", "node", "node.exe"));
        }

        var direct = directCandidates.FirstOrDefault(File.Exists);
        if (direct is not null)
        {
            return Path.GetFullPath(direct);
        }

        var versionedCandidates = new List<string>();
        foreach (var root in searchRoots.Append(Path.GetDirectoryName(harnessDirectory)!))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                versionedCandidates.AddRange(
                    Directory.EnumerateFiles(root, "node.exe", SearchOption.TopDirectoryOnly));
                versionedCandidates.AddRange(
                    Directory.EnumerateDirectories(root, "node-v*-win-x64", SearchOption.TopDirectoryOnly)
                        .Select(directory => Path.Combine(directory, "node.exe"))
                        .Where(File.Exists));
            }
            catch (UnauthorizedAccessException)
            {
                // A candidate root may be protected; continue with the private layouts.
            }
        }

        return versionedCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetExecutableVersion)
            .FirstOrDefault();
    }

    private static Version GetExecutableVersion(string executable)
    {
        try
        {
            var raw = FileVersionInfo.GetVersionInfo(executable).FileVersion;
            return Version.TryParse(raw, out var version) ? version : new Version(0, 0);
        }
        catch
        {
            return new Version(0, 0);
        }
    }
}

/// <summary>
/// Owns one local DeepSeek Harness Web process for the lifetime of the desktop
/// application. The service binds only to 127.0.0.1 and asks Windows for a
/// random free port.
/// </summary>
public sealed partial class HarnessRuntimeManager : IAsyncDisposable
{
    private const string ShutdownToken = "__DSH_DESKTOP_GRACEFUL_SHUTDOWN__";
    private const int MaximumDiagnosticLines = 80;

    private static readonly Regex ReadyUrlPattern = ReadyUrlRegex();

    private static readonly string ShutdownPreloadDataUrl = CreateShutdownPreloadDataUrl();

    private HarnessRuntimeOptions _options;
    private readonly bool _rediscoverRuntimeOnStart;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentQueue<string> _diagnosticLines = new();
    private readonly HttpClient _healthClient;

    private Process? _process;
    private WindowsProcessJob? _processJob;
    private CancellationTokenSource? _runCancellation;
    private Task<Uri>? _startupTask;
    private Uri? _baseUri;
    private int _disposed;

    public HarnessRuntimeManager()
        : this(HarnessRuntimeOptions.DiscoverDefault(), rediscoverRuntimeOnStart: true)
    {
    }

    public HarnessRuntimeManager(HarnessRuntimeOptions options)
        : this(options, rediscoverRuntimeOnStart: false)
    {
    }

    private HarnessRuntimeManager(
        HarnessRuntimeOptions options,
        bool rediscoverRuntimeOnStart)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = NormalizeOptions(options);
        _rediscoverRuntimeOnStart = rediscoverRuntimeOnStart;

        _healthClient = new HttpClient(new HttpClientHandler
        {
            // Local readiness must never be routed through a system or company proxy.
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static HarnessRuntimeOptions NormalizeOptions(HarnessRuntimeOptions options)
    {
        var runtimeDirectory = Path.GetFullPath(options.HarnessDirectory);
        var inferredSourceEntry = Path.Combine(runtimeDirectory, "apps", "cli", "src", "bin.ts");
        var inferredPackagedEntry = Path.Combine(
            runtimeDirectory,
            "node_modules",
            "@deepseek-ai",
            "dsh",
            "lib",
            "bin.js");
        var cliEntry = string.IsNullOrWhiteSpace(options.CliEntryPath)
            ? File.Exists(inferredSourceEntry) ? inferredSourceEntry : inferredPackagedEntry
            : Path.GetFullPath(options.CliEntryPath);

        return options with
        {
            HarnessDirectory = runtimeDirectory,
            NodeExecutablePath = Path.GetFullPath(options.NodeExecutablePath),
            CliEntryPath = cliEntry,
            UseTypeScriptLoader = options.UseTypeScriptLoader
                ?? string.Equals(Path.GetExtension(cliEntry), ".ts", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>The canonical loopback URL announced by Harness after it is ready.</summary>
    public Uri? BaseUri => Volatile.Read(ref _baseUri);

    public string HarnessDirectory => _options.HarnessDirectory;

    public string NodeExecutablePath => _options.NodeExecutablePath;

    public string CliEntryPath => _options.CliEntryPath!;

    public bool IsPackagedRuntime => _options.UseTypeScriptLoader is false;

    /// <summary>
    /// The effective data directory for display/diagnostics only. This manager
    /// deliberately does not create it or export DSH_HOME to the child process.
    /// </summary>
    public string UserDataDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DSH_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                : Path.GetFullPath(configured);
        }
    }

    public bool IsRunning
    {
        get
        {
            var process = Volatile.Read(ref _process);
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Receives stdout/stderr and lifecycle diagnostics. Callbacks occur on
    /// background threads; UI subscribers must marshal to the dispatcher.
    /// </summary>
    public event EventHandler<string>? LogReceived;

    public event EventHandler<Uri>? Ready;

    /// <summary>
    /// Starts either the portable npm CLI or checked-out official source as:
    /// node [--import tsx/esm] &lt;entry&gt; web --no-open --port 0
    /// and completes only after the announced root URL returns HTTP 200.
    /// Concurrent callers share the same startup operation.
    /// </summary>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Task<Uri> startupTask;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null && IsProcessAlive(_process) && BaseUri is { } runningUri)
            {
                return runningUri;
            }

            if (_startupTask is { IsCompleted: false } activeStartup)
            {
                startupTask = activeStartup;
            }
            else
            {
                DisposeStaleRuntimeLocked();
                if (_rediscoverRuntimeOnStart)
                {
                    // A portable update atomically switches current.json while
                    // this desktop process remains alive. Re-resolving here
                    // makes the next StartAsync boot the newly activated release.
                    _options = NormalizeOptions(HarnessRuntimeOptions.DiscoverDefault());
                }

                ValidateRuntimeFiles();
                startupTask = StartCoreAsync();
                _startupTask = startupTask;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        return await startupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests the CLI's own bounded SIGTERM disposal over its private stdin
    /// control channel, then escalates to job/process-tree termination only if
    /// graceful shutdown does not finish in time.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        WindowsProcessJob? processJob;
        CancellationTokenSource? runCancellation;

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            process = _process;
            processJob = _processJob;
            runCancellation = _runCancellation;

            _process = null;
            _processJob = null;
            _runCancellation = null;
            _startupTask = null;
            Volatile.Write(ref _baseUri, null);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        runCancellation?.Cancel();

        if (process is null)
        {
            processJob?.Dispose();
            runCancellation?.Dispose();
            return;
        }

        try
        {
            await StopProcessAsync(process, processJob, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            processJob?.Dispose();
            runCancellation?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _healthClient.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private async Task<Uri> StartCoreAsync()
    {
        var readyUrl = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCancellation = new CancellationTokenSource();
        var process = CreateHarnessProcess(readyUrl);
        WindowsProcessJob? processJob = null;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 DeepSeek Harness 后台进程。");
            }

            processJob = WindowsProcessJob.TryAttach(process, message => PublishLog("desktop", message));
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _processJob = processJob;
            _runCancellation = runCancellation;

            PublishLog(
                "desktop",
                $"Harness 已启动（PID {process.Id.ToString(CultureInfo.InvariantCulture)}），正在等待随机端口。");

            using var startupTimeout = new CancellationTokenSource(_options.StartupTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                startupTimeout.Token,
                runCancellation.Token);

            Uri announcedUri;
            try
            {
                announcedUri = await readyUrl.Task
                    .WaitAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);

                await WaitForHttpOkAsync(process, announcedUri, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startupTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"DeepSeek Harness 在 {_options.StartupTimeout.TotalSeconds:0} 秒内未准备完成。"
                    + FormatRecentDiagnostics());
            }

            Volatile.Write(ref _baseUri, announcedUri);
            PublishLog("desktop", $"Harness 已就绪：{announcedUri}");
            PublishReady(announcedUri);
            return announcedUri;
        }
        catch
        {
            await ClearFailedStartAsync(process, processJob, runCancellation).ConfigureAwait(false);
            throw;
        }
    }

    private Process CreateHarnessProcess(TaskCompletionSource<Uri> readyUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.NodeExecutablePath,
            WorkingDirectory = _options.HarnessDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // A data-URL preload provides a Windows-safe, private control channel.
        // It emits SIGTERM inside Node, so the official CLI executes its own
        // five-second Loader/plugin disposal path before the desktop escalates.
        startInfo.ArgumentList.Add("--import");
        startInfo.ArgumentList.Add(ShutdownPreloadDataUrl);
        if (_options.UseTypeScriptLoader is true)
        {
            startInfo.ArgumentList.Add("--import");
            startInfo.ArgumentList.Add("tsx/esm");
        }

        startInfo.ArgumentList.Add(_options.CliEntryPath!);
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--no-open");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");

        // The portable release includes pnpm in node_modules/.bin. The official
        // plugin manager resolves `pnpm` from PATH, so expose only the private
        // Node/toolchain before the inherited machine PATH. This keeps plugin
        // installation working on a clean PC without a global Node or pnpm.
        var privatePathEntries = new[]
        {
            Path.GetDirectoryName(_options.NodeExecutablePath),
            Path.Combine(_options.HarnessDirectory, "node_modules", ".bin"),
        }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var inheritedPath = startInfo.Environment.TryGetValue("PATH", out var existingPath)
            ? existingPath
            : Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            privatePathEntries.Append(inheritedPath).Where(path => !string.IsNullOrWhiteSpace(path)));

        // Do not set DSH_HOME here. The inherited environment intentionally
        // preserves the user's existing ~/.dsh API keys, settings and plugins.

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            PublishLog("stdout", args.Data);
            var match = ReadyUrlPattern.Match(args.Data);
            if (match.Success
                && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri)
                && IsTrustedLoopbackUri(uri))
            {
                readyUrl.TrySetResult(NormalizeStartupUri(uri));
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                PublishLog("stderr", args.Data);
            }
        };

        process.Exited += (_, _) =>
        {
            Volatile.Write(ref _baseUri, null);
            if (!readyUrl.Task.IsCompleted)
            {
                var exitCode = TryGetExitCode(process);
                readyUrl.TrySetException(new InvalidOperationException(
                    $"DeepSeek Harness 在准备完成前退出（退出码 {exitCode}）。"
                    + FormatRecentDiagnostics()));
            }
            else
            {
                PublishLog("desktop", $"Harness 后台进程已退出（退出码 {TryGetExitCode(process)}）。");
            }
        };

        return process;
    }

    private async Task WaitForHttpOkAsync(
        Process process,
        Uri uri,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessAlive(process))
            {
                throw new InvalidOperationException(
                    $"DeepSeek Harness 在健康检查期间退出（退出码 {TryGetExitCode(process)}）。"
                    + FormatRecentDiagnostics());
            }

            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                };

                using var response = await _healthClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                PublishLog(
                    "desktop",
                    $"Harness 健康检查暂时返回 HTTP {(int)response.StatusCode}。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A single local request timed out; retry within the global startup budget.
            }
            catch (HttpRequestException)
            {
                // The URL can be announced immediately before HTTP accepts the first request.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopProcessAsync(
        Process process,
        WindowsProcessJob? processJob,
        CancellationToken cancellationToken)
    {
        if (!IsProcessAlive(process))
        {
            return;
        }

        PublishLog("desktop", "正在让 Harness 保存状态并关闭后台服务……");
        try
        {
            process.StandardInput.WriteLine(ShutdownToken);
            process.StandardInput.Flush();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException)
        {
            PublishLog("desktop", $"无法投递优雅关闭请求：{exception.Message}");
        }

        var exitedGracefully = await WaitForExitWithinAsync(
            process,
            _options.GracefulShutdownTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!exitedGracefully)
        {
            PublishLog("desktop", "Harness 未在宽限期内退出，正在清理后台进程树。");

            // Closing a KILL_ON_JOB_CLOSE job removes descendants even when the
            // Node leader has already exited. Kill(entireProcessTree) is the
            // fallback for hosts that do not permit assigning a nested job.
            processJob?.Dispose();
            if (IsProcessAlive(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or NotSupportedException
                        or Win32Exception)
                {
                    PublishLog("desktop", $"进程树清理请求返回：{exception.Message}");
                }
            }

            await WaitForExitWithinAsync(
                process,
                _options.ForcedShutdownTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }

        // Dispose after a graceful exit as well, so an unexpected detached
        // descendant cannot outlive the desktop application.
        processJob?.Dispose();
        PublishLog("desktop", "Harness 后台服务已关闭。");
    }

    private async Task ClearFailedStartAsync(
        Process process,
        WindowsProcessJob? processJob,
        CancellationTokenSource runCancellation)
    {
        var ownsRuntime = false;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_process, process))
            {
                ownsRuntime = true;
                _process = null;
                _processJob = null;
                _runCancellation = null;
                _startupTask = null;
                Volatile.Write(ref _baseUri, null);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        // StopAsync may already have detached this exact runtime. In that case
        // it owns cancellation, process disposal and job closure; touching the
        // same Process concurrently would turn a normal exit into an exception.
        if (!ownsRuntime)
        {
            return;
        }

        runCancellation.Cancel();
        try
        {
            await StopProcessAsync(process, processJob, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            processJob?.Dispose();
            runCancellation.Dispose();
        }
    }

    /// <summary>Called only while _lifecycleGate is held.</summary>
    private void DisposeStaleRuntimeLocked()
    {
        if (_process is null)
        {
            return;
        }

        if (IsProcessAlive(_process))
        {
            throw new InvalidOperationException(
                "Harness 后台进程正在运行，但尚未发布可用地址，请稍后重试。");
        }

        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
        _processJob?.Dispose();
        _processJob = null;
        _process.Dispose();
        _process = null;
        _startupTask = null;
        Volatile.Write(ref _baseUri, null);
    }

    private static async Task<bool> WaitForExitWithinAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsProcessAlive(process))
        {
            return true;
        }

        try
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                cancellationToken);
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !IsProcessAlive(process);
        }
    }

    private void ValidateRuntimeFiles()
    {
        if (!File.Exists(_options.NodeExecutablePath))
        {
            throw new FileNotFoundException("找不到 Node.js 运行程序。", _options.NodeExecutablePath);
        }

        var cliEntry = _options.CliEntryPath!;
        if (!File.Exists(cliEntry))
        {
            throw new FileNotFoundException("找不到 DeepSeek Harness CLI 入口。", cliEntry);
        }

        if (_options.UseTypeScriptLoader is not true)
        {
            return;
        }

        var tsxLoader = Path.Combine(
            _options.HarnessDirectory,
            "node_modules",
            "tsx",
            "dist",
            "esm",
            "index.mjs");
        if (!File.Exists(tsxLoader))
        {
            throw new FileNotFoundException(
                "Harness 依赖尚未安装（缺少 tsx/esm）。请先完成官方依赖安装和构建。",
                tsxLoader);
        }
    }

    private void PublishLog(string channel, string message)
    {
        var line = $"[{channel}] {RedactLaunchToken(message)}";
        _diagnosticLines.Enqueue(line);
        while (_diagnosticLines.Count > MaximumDiagnosticLines
               && _diagnosticLines.TryDequeue(out _))
        {
        }

        var handlers = LogReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, line);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Harness log subscriber failed: {exception}");
            }
        }
    }

    private void PublishReady(Uri uri)
    {
        var handlers = Ready;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<Uri> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, uri);
            }
            catch (Exception exception)
            {
                // A UI observer must not turn a healthy local service into a
                // failed startup transaction.
                Debug.WriteLine($"Harness ready subscriber failed: {exception}");
            }
        }
    }

    private string FormatRecentDiagnostics()
    {
        var lines = _diagnosticLines.ToArray();
        return lines.Length == 0
            ? string.Empty
            : Environment.NewLine + string.Join(Environment.NewLine, lines.TakeLast(12));
    }

    private static bool IsProcessAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited
                ? process.ExitCode.ToString(CultureInfo.InvariantCulture)
                : "未知";
        }
        catch
        {
            return "未知";
        }
    }

    private static bool IsTrustedLoopbackUri(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && IPAddress.TryParse(uri.Host, out var address)
            && IPAddress.IsLoopback(address);
    }

    internal static string RedactLaunchToken(string value) =>
        Regex.Replace(value, @"([?&]token=)[^\s&#()]+", "$1[redacted]", RegexOptions.IgnoreCase);

    internal static Uri NormalizeStartupUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Path = "/",
            // Since Harness 0.1.2 the launch URL exchanges this query token for
            // an HttpOnly session cookie. Keep it for health checks AND WebView;
            // each has its own cookie jar. Never disable upstream authentication.
            Query = uri.Query.TrimStart('?'),
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static string CreateShutdownPreloadDataUrl()
    {
        const string source = """
            const token = '__DSH_DESKTOP_GRACEFUL_SHUTDOWN__';
            let buffered = '';
            let requested = false;
            const requestShutdown = () => {
              if (requested) return;
              requested = true;
              const deliver = () => {
                if (process.listenerCount('SIGTERM') > 0) process.emit('SIGTERM');
                else setTimeout(deliver, 25);
              };
              deliver();
            };
            process.stdin.setEncoding('utf8');
            process.stdin.on('data', chunk => {
              buffered = (buffered + chunk).slice(-256);
              if (buffered.includes(token)) requestShutdown();
            });
            process.stdin.on('end', requestShutdown);
            process.stdin.unref?.();
            """;

        return "data:text/javascript;base64,"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    [GeneratedRegex(
        @"(?:^|\s)dsh\s+web:\s*(?<url>http://127\.0\.0\.1:\d+(?:/[^\s()]*)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReadyUrlRegex();

    /// <summary>
    /// A kill-on-close job makes the desktop process the authoritative owner
    /// of the complete Node/helper tree, including on application crashes.
    /// </summary>
    private sealed class WindowsProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private nint _handle;

        private WindowsProcessJob(nint handle)
        {
            _handle = handle;
        }

        public static WindowsProcessJob? TryAttach(Process process, Action<string> report)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            nint handle = CreateJobObjectW(nint.Zero, null);
            if (handle == nint.Zero)
            {
                report($"无法创建进程作业对象：{new Win32Exception().Message}");
                return null;
            }

            var job = new WindowsProcessJob(handle);
            try
            {
                var information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };

                var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                    if (!SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            buffer,
                            (uint)size))
                    {
                        throw new Win32Exception();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (!AssignProcessToJobObject(handle, process.Handle))
                {
                    throw new Win32Exception();
                }

                return job;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                report($"无法把 Harness 加入桌面进程作业对象：{exception.Message}");
                job.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, nint.Zero);
            if (handle != nint.Zero)
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            nint job,
            int informationClass,
            nint jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}
