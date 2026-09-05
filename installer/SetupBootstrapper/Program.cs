using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace DeepSeekHarnessDesktopSetup;

internal static class Program
{
    private const string PayloadResourceName = "DeepSeekHarnessDesktop.OfflinePayload";
    private const string ProductFolderName = "DeepSeekHarnessDesktop";
    private const string DesktopExeName = "DeepSeekHarnessDesktop.exe";
    private static string? _logPath;

    [STAThread]
    private static void Main(string[] args)
    {
        // Exercise the very same extraction and installation transaction without
        // UI, shortcuts or launching the app, in a marked disposable directory.
        if (args.Length == 2 && args[0] is "--self-test-install" or "--self-test-ui")
        {
            try
            {
                var testRoot = Path.GetFullPath(args[1]);
                var marker = Path.Combine(testRoot, ".installer-test-root");
                if (Directory.Exists(testRoot) && Directory.EnumerateFileSystemEntries(testRoot).Any() && !File.Exists(marker))
                    throw new InvalidOperationException("Self-test target must be empty or carry its test marker.");
                Directory.CreateDirectory(testRoot);
                File.WriteAllText(marker, "DeepSeekHarnessDesktop installer test fixture");
                if (args[0] == "--self-test-ui")
                {
                    InitializeUi();
                    Application.Run(new InstallProgressForm(
                        progress => Install(testRoot, integrateDesktop: false, progress),
                        () => _logPath, Path.Combine(testRoot, "ui-test-output")));
                }
                else
                {
                    Install(testRoot, integrateDesktop: false,
                        progress => WriteLog($"PROGRESS {progress.Percent}% {progress.Stage}: {progress.Detail}"));
                    Environment.ExitCode = 0;
                }
            }
            catch (Exception error)
            {
                WriteLog(error.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        InitializeUi();
        Application.Run(new InstallProgressForm(
            progress => Install(null, integrateDesktop: true, progress), () => _logPath));
    }

    private static void InitializeUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    private static bool Install(string? explicitRoot, bool integrateDesktop, Action<SetupProgress>? onProgress = null)
    {
        var progress = new SetupProgressReporter(onProgress);
        progress.Report("检查安装环境", 1, "检查安装目录和运行中的程序…");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法定位当前用户的本地应用数据目录。");
        }

        var installRoot = explicitRoot ?? Path.Combine(localAppData, ProductFolderName);
        var lockId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(installRoot).ToUpperInvariant())))[..24];
        using var setupLock = new Mutex(false, "Local\\DSH-Setup-" + lockId);
        var lockTaken = false;
        try { lockTaken = setupLock.WaitOne(0); }
        catch (AbandonedMutexException) { lockTaken = true; }
        if (!lockTaken) throw new InvalidOperationException("此目录已有安装程序正在运行，请等待它结束。");
        try
        {
            Directory.CreateDirectory(installRoot);
            var logs = Path.Combine(installRoot, "setup-logs");
            Directory.CreateDirectory(logs);
            _logPath = Path.Combine(logs, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            WriteLog("Starting setup; integrateDesktop=" + integrateDesktop);
            if (integrateDesktop) EnsureApplicationIsNotRunning();
            var temporaryRoot = Path.Combine(installRoot, ".setup-" + Guid.NewGuid().ToString("N"));

            try
            {
                WriteLog("Extracting payload: " + temporaryRoot);
                progress.Report("解压离线安装包", 5, "准备读取内嵌安装文件…");
                ExtractPayloadWithProgress(temporaryRoot, progress);

                var payloadRoot = Path.Combine(temporaryRoot, "payload");
                var incomingApp = Path.Combine(payloadRoot, "app");
                var incomingRuntime = Path.Combine(payloadRoot, "runtime");
                var incomingWebView = Path.Combine(payloadRoot, "webview2");
                progress.Report("校验安装文件", 57, "检查桌面程序、Windows 运行环境和后台入口…");
                ValidatePayload(incomingApp, incomingRuntime, incomingWebView);
                var runtimeDestination = Path.Combine(installRoot, "runtime");
                var needsRuntime = !IsValidRuntime(runtimeDestination);
                static long TreeBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                var copyTotal = TreeBytes(incomingApp) + TreeBytes(incomingWebView) + (needsRuntime ? TreeBytes(incomingRuntime) : 0);
                long copiedBytes = 0;
                void OnCopied(long bytes)
                {
                    copiedBytes += bytes;
                    progress.Report("复制 Windows 安装文件", 60 + (int)(30d * copiedBytes / Math.Max(1, copyTotal)),
                        $"已复制 {SetupProgressReporter.FormatBytes(copiedBytes)} / {SetupProgressReporter.FormatBytes(copyTotal)}");
                }
                progress.Report("复制 Windows 安装文件", 60, $"需要复制 {SetupProgressReporter.FormatBytes(copyTotal)}，正在准备文件…");
                // Do not rename payload/app: a scanner can temporarily hold the newly
                // extracted directory. Copy into complete candidates before activation.
                var candidateApp = Path.Combine(temporaryRoot, "ready-app");
                WriteLog("Preparing application candidate");
                DirectoryDeployment.CopyTree(incomingApp, candidateApp, WriteLog, OnCopied);
                DirectoryDeployment.CopyTree(incomingWebView, Path.Combine(candidateApp, "webview2"), WriteLog, OnCopied);
                string? candidateRuntime = null;
                if (needsRuntime)
                {
                    WriteLog("Preparing runtime candidate");
                    candidateRuntime = Path.Combine(temporaryRoot, "ready-runtime");
                    DirectoryDeployment.CopyTree(incomingRuntime, candidateRuntime, WriteLog, OnCopied);
                }
                else WriteLog("Keeping existing valid runtime and user data");
                if (integrateDesktop) EnsureApplicationIsNotRunning();
                var changes = new List<DirectoryDeployment.Change>();
                try
                {
                    progress.Report("启用安装文件", 92, "替换程序文件并保留可恢复的旧文件…");
                    if (candidateRuntime is not null)
                        changes.Add(DirectoryDeployment.Activate(candidateRuntime, runtimeDestination, WriteLog));
                    var appDestination = Path.Combine(installRoot, "app");
                    changes.Add(DirectoryDeployment.Activate(candidateApp, appDestination, WriteLog));
                    ValidatePayload(appDestination, runtimeDestination, Path.Combine(appDestination, "webview2"));
                    progress.Report("配置桌面快捷方式", 94, "安装文件已验证，现有用户配置保持不变。");
                    if (integrateDesktop) CreateDesktopShortcut(Path.Combine(appDestination, DesktopExeName), appDestination);
                    WriteLog("Installation transaction committed");
                }
                catch (Exception installError)
                {
                    var errors = new List<Exception> { installError };
                    foreach (var change in changes.AsEnumerable().Reverse())
                        try { DirectoryDeployment.Rollback(change, WriteLog); }
                        catch (Exception rollbackError) { errors.Add(rollbackError); }
                    throw new AggregateException("安装替换失败，已尝试恢复旧文件；请查看日志。", errors);
                }
            }
            catch (Exception error) { WriteLog("INSTALL FAILED: " + error); throw; }
            finally
            {
                // Cleanup is best-effort and MUST NOT replace the primary exception
                // or report a successfully committed installation as a failure.
                progress.Report("清理临时安装文件", 96, "正在收尾，请稍候。大型离线包的清理可能需要一些时间。");
                DirectoryDeployment.TryCleanup(temporaryRoot, installRoot, WriteLog);
            }
            // Launch only after cleanup, from the installed directory, never the
            // extraction tree. Failure to launch does not undo a valid installation.
            var launched = false;
            if (integrateDesktop)
            {
                progress.Report("启动 DeepSeek Harness Desktop", 99, "安装已完成，正在启动桌面程序…");
                var appDestination = Path.Combine(installRoot, "app");
                try { launched = Process.Start(new ProcessStartInfo(Path.Combine(appDestination, DesktopExeName)) { UseShellExecute = true, WorkingDirectory = appDestination }) is not null; }
                catch (Exception error) { WriteLog("Launch warning (installation completed): " + error); }
            }
            WriteLog("SETUP SUCCESS");
            progress.Report("安装完成", 100, "安装已完成。", force: true);
            return launched;
        }
        finally { setupLock.ReleaseMutex(); }
    }

    private static void WriteLog(string message)
    {
        if (_logPath is null) return;
        try { File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8); }
        catch { /* Logging must never replace the installation error. */ }
    }

    private static void EnsureApplicationIsNotRunning()
    {
        var running = Process.GetProcessesByName("DeepSeekHarnessDesktop");
        if (running.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException("请先从右下角托盘图标退出 DeepSeek Harness Desktop，再重新运行安装程序。");
    }

    private static void ExtractPayload(string temporaryRoot) => ExtractPayloadWithProgress(temporaryRoot, null);

    private static void ExtractPayloadWithProgress(string temporaryRoot, SetupProgressReporter? progress)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
        if (payload is null)
        {
            throw new InvalidOperationException("安装包中的离线文件不完整。请重新下载完整安装程序。");
        }

        Directory.CreateDirectory(temporaryRoot);
        var normalizedRoot = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var gzip = new GZipStream(payload, CompressionMode.Decompress);
        var header = new byte[512];
        string? pendingPaxPath = null;
        while (ReadTarBlock(gzip, header))
        {
            if (header.All(value => value == 0))
            {
                return;
            }

            var entryType = (char)header[156];
            var entryLength = ReadTarNumber(header.AsSpan(124, 12));
            if (entryLength < 0)
            {
                throw new InvalidOperationException("安装包包含无效的文件长度。");
            }

            if (entryType == 'x')
            {
                pendingPaxPath = ReadPaxPath(gzip, entryLength);
                SkipTarPadding(gzip, entryLength);
                continue;
            }

            var relativeName = (pendingPaxPath ?? GetTarEntryName(header))
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            pendingPaxPath = null;
            if (string.IsNullOrWhiteSpace(relativeName))
            {
                throw new InvalidOperationException("安装包包含无效的文件路径。");
            }

            var targetPath = Path.GetFullPath(Path.Combine(temporaryRoot, relativeName));
            if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装包包含不安全的文件路径。");
            }

            if (entryType == '5')
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entryType is '1' or '2')
            {
                throw new InvalidOperationException("安装包不能包含链接文件。");
            }

            if (entryType is not ('\0' or '0'))
            {
                throw new InvalidOperationException("安装包包含不支持的文件类型。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyTarData(gzip, output, entryLength, () => progress?.Report(
                "解压离线安装包", 5 + (int)(50d * payload.Position / Math.Max(1, payload.Length)),
                $"已读取安装包 {SetupProgressReporter.FormatBytes(payload.Position)} / {SetupProgressReporter.FormatBytes(payload.Length)}"));
            SkipTarPadding(gzip, entryLength);
        }

        throw new InvalidOperationException("安装包意外结束。");
    }

    private static string? ReadPaxPath(Stream source, long length)
    {
        if (length < 0 || length > 1024 * 1024)
        {
            throw new InvalidOperationException("安装包包含异常的 PAX 扩展头。");
        }

        var data = new byte[(int)length];
        if (!ReadTarBlock(source, data))
        {
            throw new InvalidOperationException("安装包中的 PAX 扩展头不完整。");
        }

        foreach (var line in Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(' ');
            if (separator < 0 || separator == line.Length - 1)
            {
                continue;
            }

            var attribute = line[(separator + 1)..];
            const string pathPrefix = "path=";
            if (attribute.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                return attribute[pathPrefix.Length..];
            }
        }

        return null;
    }

    private static bool ReadTarBlock(Stream source, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = source.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new InvalidOperationException("安装包中的 TAR 文件不完整。");
            }

            offset += read;
        }

        return true;
    }

    private static string GetTarEntryName(byte[] header)
    {
        var name = ReadTarText(header.AsSpan(0, 100));
        var prefix = ReadTarText(header.AsSpan(345, 155));
        return string.IsNullOrWhiteSpace(prefix) ? name : prefix + "/" + name;
    }

    private static string ReadTarText(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        return Encoding.UTF8.GetString(bytes[..end]).TrimEnd(' ');
    }

    private static long ReadTarNumber(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        if ((bytes[0] & 0x80) != 0)
        {
            long value = bytes[0] & 0x7f;
            for (var index = 1; index < bytes.Length; index++)
            {
                value = checked((value << 8) | bytes[index]);
            }

            return value;
        }

        long result = 0;
        foreach (var value in bytes)
        {
            if (value is 0 or (byte)' ')
            {
                continue;
            }

            if (value is < (byte)'0' or > (byte)'7')
            {
                throw new InvalidOperationException("安装包包含无效的 TAR 数字字段。");
            }

            result = checked((result << 3) + value - (byte)'0');
        }

        return result;
    }

    private static void CopyTarData(Stream source, Stream destination, long length, Action? onChunk = null)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new InvalidOperationException("安装包中的文件内容不完整。");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
            onChunk?.Invoke();
        }
    }

    private static void SkipTarPadding(Stream source, long length)
    {
        var padding = (512 - (length % 512)) % 512;
        if (padding == 0)
        {
            return;
        }

        var discarded = new byte[padding];
        if (!ReadTarBlock(source, discarded))
        {
            throw new InvalidOperationException("安装包中的文件填充不完整。");
        }
    }

    private static void ValidatePayload(string appRoot, string runtimeRoot, string webViewRoot)
    {
        if (!File.Exists(Path.Combine(appRoot, DesktopExeName)))
        {
            throw new InvalidOperationException("安装包中缺少桌面程序文件。");
        }

        if (!IsValidRuntime(runtimeRoot))
        {
            throw new InvalidOperationException("安装包中缺少可用的 Harness 运行环境。");
        }

        if (!File.Exists(Path.Combine(webViewRoot, "msedgewebview2.exe")))
        {
            throw new InvalidOperationException("安装包中缺少浏览器运行环境。");
        }
    }

    private static bool IsValidRuntime(string runtimeRoot)
    {
        try
        {
            var manifestPath = Path.Combine(runtimeRoot, "current.json");
            var nodePath = Path.Combine(runtimeRoot, "node", "node.exe");
            if (!File.Exists(manifestPath) || !File.Exists(nodePath))
            {
                return false;
            }

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifestDocument.RootElement;
            var releaseDirectory = root.GetProperty("releaseDirectory").GetString();
            var entryPoint = root.GetProperty("entryPoint").GetString();
            if (string.IsNullOrWhiteSpace(releaseDirectory) || string.IsNullOrWhiteSpace(entryPoint))
            {
                return false;
            }

            var normalizedRoot = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var entryPath = Path.GetFullPath(Path.Combine(runtimeRoot, releaseDirectory, entryPoint));
            return entryPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(entryPath);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateDesktopShortcut(string executablePath, string workingDirectory)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new InvalidOperationException("无法创建桌面快捷方式。");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "DeepSeek Harness Desktop.lnk"));
        shortcut.TargetPath = executablePath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = executablePath + ",0";
        shortcut.Description = "DeepSeek Harness Desktop";
        shortcut.Save();
    }
}
