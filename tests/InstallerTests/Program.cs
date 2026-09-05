using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DeepSeekHarnessDesktopSetup;

var root = Path.Combine(Path.GetTempPath(), "DSH-Installer-Unit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var logs = new List<string>();
var updates = new List<SetupProgress>();
var reporter = new SetupProgressReporter(updates.Add);
reporter.Report("Start", 1, "");
reporter.Report("Copy", 60, "");
reporter.Report("Cleanup", 20, "", force: true);
reporter.Report("Done", 100, "", force: true);
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
string Fixture(string name, string content)
{
    var dir = Path.Combine(root, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "app.txt"), content);
    return dir;
}
Assert(updates.Count == 4 && updates[2].Percent == 60 && updates[^1].Percent == 100, "Stage progress is bounded and never goes backward");
Assert(SetupProgressReporter.FormatBytes(1048576) == "1.0 MB", "Byte counts use explicit readable units");

var incoming = Fixture("payload-app", "new");
var candidate = Path.Combine(root, "candidate");
var destination = Path.Combine(root, "installed");
using (var sourceLock = Native.OpenDirectory(incoming))
using (var fileLock = new FileStream(Path.Combine(incoming, "app.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    Assert(!sourceLock.IsInvalid, "Create a real Windows directory lock");
    var oldMoveFailed = false;
    try { Directory.Move(incoming, destination); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { oldMoveFailed = true; }
    Assert(oldMoveFailed, "Reproduce old payload directory rename failure");
    long copied = 0;
    DirectoryDeployment.CopyTree(incoming, candidate, logs.Add, bytes => copied += bytes);
    Assert(copied == new FileInfo(Path.Combine(incoming, "app.txt")).Length, "Copy progress counts successful file bytes exactly once");
    DirectoryDeployment.Activate(candidate, destination, logs.Add);
    Assert(File.ReadAllText(Path.Combine(destination, "app.txt")) == "new", "Install succeeds while extracted source directory is locked");
}

var next = Fixture("next", "updated");
var change = DirectoryDeployment.Activate(next, destination, logs.Add);
Assert(change.Backup is not null && File.Exists(Path.Combine(change.Backup, "app.txt")), "Upgrade retains old directory for rollback");
DirectoryDeployment.Rollback(change, logs.Add);
Assert(File.ReadAllText(Path.Combine(destination, "app.txt")) == "new", "Rollback restores previous bytes");

var failing = Fixture("failing", "bad");
var failed = false;
try
{
    DirectoryDeployment.Activate(failing, destination, logs.Add, (source, target) =>
    {
        if (source == failing) throw new UnauthorizedAccessException("Injected activation failure");
        Directory.Move(source, target);
    });
}
catch (UnauthorizedAccessException) { failed = true; }
Assert(failed && File.ReadAllText(Path.Combine(destination, "app.txt")) == "new", "Failed activation restores old app, not a partial install");

var transient = Fixture("transient", "retry");
var attempts = 0;
DirectoryDeployment.Activate(transient, Path.Combine(root, "retry-result"), logs.Add, (source, target) =>
{
    if (++attempts < 3) throw new UnauthorizedAccessException("Temporary scanner lock");
    Directory.Move(source, target);
});
Assert(attempts == 3, "Transient access denial is retried");

var cleanup = Fixture(".setup-locked-cleanup", "temporary");
using (var cleanupLock = Native.OpenDirectory(cleanup))
{
    Assert(!DirectoryDeployment.TryCleanup(cleanup, root, logs.Add), "Locked cleanup is a warning, not an installation failure");
}
Assert(!DirectoryDeployment.TryCleanup(destination, root, logs.Add) && File.Exists(Path.Combine(destination, "app.txt")), "Cleanup refuses installed app directories");
Console.WriteLine("PASS All installer transaction tests");

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    internal static SafeFileHandle OpenDirectory(string path) => CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
}
