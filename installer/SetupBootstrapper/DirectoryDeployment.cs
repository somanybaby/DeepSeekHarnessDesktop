namespace DeepSeekHarnessDesktopSetup;

/// <summary>Prepare by copying, activate with bounded retries, and retain rollback directories.</summary>
internal static class DirectoryDeployment
{
    internal sealed record Change(string Destination, string? Backup);

    internal static void Retry(Action action, Action<string> log, string operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 5)
            {
                log($"Retry {attempt + 1}: {operation}: {error.Message}");
                Thread.Sleep(Math.Min(150 * (1 << attempt), 1000));
            }
        }
    }

    internal static void CopyTree(string source, string destination, Action<string> log, Action<long>? copied = null)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装文件不能包含目录链接：" + source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装文件不能包含文件链接：" + file);
            var target = Path.Combine(destination, Path.GetFileName(file));
            // Only the disposable candidate tree is written here; an interrupted
            // copy may leave a partial target which must be replaceable on retry.
            Retry(() => File.Copy(file, target, overwrite: true), log, "copy " + file);
            copied?.Invoke(new FileInfo(file).Length);
        }
        foreach (var child in Directory.EnumerateDirectories(source))
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)), log, copied);
    }

    internal static Change Activate(string candidate, string destination, Action<string> log,
        Action<string, string>? move = null)
    {
        move ??= Directory.Move;
        var backup = Directory.Exists(destination) ? destination + ".previous-" + Guid.NewGuid().ToString("N") : null;
        if (backup is not null) Retry(() => move(destination, backup), log, "backup " + destination);
        try
        {
            Retry(() => move(candidate, destination), log, "activate " + destination);
            return new Change(destination, backup);
        }
        catch (Exception installError)
        {
            if (backup is not null)
            {
                try { Retry(() => move(backup, destination), log, "restore " + destination); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("安装替换失败；旧文件保留于 " + backup, installError, rollbackError);
                }
            }
            throw;
        }
    }

    internal static void Rollback(Change change, Action<string> log)
    {
        // Never delete a failed candidate or the old installation during rollback.
        if (Directory.Exists(change.Destination))
            Retry(() => Directory.Move(change.Destination, change.Destination + ".failed-" + Guid.NewGuid().ToString("N")), log, "quarantine failed install");
        if (change.Backup is not null)
            Retry(() => Directory.Move(change.Backup, change.Destination), log, "restore previous install");
    }

    internal static bool TryCleanup(string path, string ownerRoot, Action<string> log)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(ownerRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullPath).StartsWith(".setup-") is false)
                throw new IOException("Refusing cleanup outside this setup operation.");
            if (Directory.Exists(fullPath))
                Retry(() => Directory.Delete(fullPath, recursive: true), log, "cleanup " + fullPath);
            return true;
        }
        catch (Exception error)
        {
            log("Cleanup warning (installation result unchanged): " + error);
            return false;
        }
    }
}
