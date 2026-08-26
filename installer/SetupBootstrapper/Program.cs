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

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Install();
            MessageBox.Show(
                "安装完成。程序已启动。\r\n\r\n首次安装的 API 配置为空，请在程序底部的“API 配置”中添加。",
                "DeepSeek Harness Desktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "安装未完成：\r\n" + exception.Message,
                "DeepSeek Harness Desktop 安装程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Install()
    {
        EnsureApplicationIsNotRunning();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法定位当前用户的本地应用数据目录。");
        }

        var installRoot = Path.Combine(localAppData, ProductFolderName);
        Directory.CreateDirectory(installRoot);
        var temporaryRoot = Path.Combine(installRoot, ".setup-" + Guid.NewGuid().ToString("N"));

        try
        {
            ExtractPayload(temporaryRoot);

            var payloadRoot = Path.Combine(temporaryRoot, "payload");
            var incomingApp = Path.Combine(payloadRoot, "app");
            var incomingRuntime = Path.Combine(payloadRoot, "runtime");
            var incomingWebView = Path.Combine(payloadRoot, "webview2");
            ValidatePayload(incomingApp, incomingRuntime, incomingWebView);

            if (Directory.Exists(incomingWebView))
            {
                MoveDirectory(incomingWebView, Path.Combine(incomingApp, "webview2"), overwrite: true);
            }

            var runtimeDestination = Path.Combine(installRoot, "runtime");
            if (!IsValidRuntime(runtimeDestination))
            {
                MoveDirectory(incomingRuntime, runtimeDestination, overwrite: true);
            }

            var appDestination = Path.Combine(installRoot, "app");
            MoveDirectory(incomingApp, appDestination, overwrite: true);

            var executablePath = Path.Combine(appDestination, DesktopExeName);
            CreateDesktopShortcut(executablePath, appDestination);
            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
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

    private static void ExtractPayload(string temporaryRoot)
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
            CopyTarData(gzip, output, entryLength);
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

    private static void CopyTarData(Stream source, Stream destination, long length)
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

    private static void MoveDirectory(string source, string destination, bool overwrite)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("找不到待安装的目录：" + source);
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("安装目标无效。");
        Directory.CreateDirectory(parent);
        var backup = destination + ".previous-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var hadExisting = Directory.Exists(destination);

        if (hadExisting)
        {
            if (!overwrite)
            {
                throw new IOException("目标目录已存在：" + destination);
            }

            Directory.Move(destination, backup);
        }

        try
        {
            Directory.Move(source, destination);
        }
        catch
        {
            if (hadExisting && Directory.Exists(backup) && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }

            throw;
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
