using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeepSeekHarnessDesktop;
using DeepSeekHarnessDesktop.Services;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

Assert(UpdateService.SelectProcessDiagnostic(
    "file:///pnpm.mjs:123\n throw new Error(\"readStream must be readable\");\n ^\nError: readStream must be readable\n at test.js:1\nNode.js v22.23.2", "")
    == "Error: readStream must be readable", "Display the actual error, not Node version");
Assert(UpdateService.SelectProcessDiagnostic("", "progress\nERR_PNPM_FETCH_403 Forbidden")
    == "ERR_PNPM_FETCH_403 Forbidden", "Detect pnpm errors from stdout");
Assert(UpdateService.SelectProcessDiagnostic("\u001b[31mTypeError: broken\u001b[0m\nNode.js v22", "")
    == "TypeError: broken", "Remove ANSI coloring");
Assert(UpdateService.SelectProcessDiagnostic("Node.js v22.23.2", "").Contains("日志"), "Do not use a lone Node footer");
Assert(HarnessRuntimeManager.NormalizeStartupUri(new Uri("http://127.0.0.1:1234/?token=test-token")).Query == "?token=test-token", "Preserve bootstrap authentication for each cookie jar");
Assert(!HarnessRuntimeManager.RedactLaunchToken("dsh web: http://127.0.0.1:1234/?token=test-token").Contains("test-token"), "Launch tokens never appear in runtime logs");
using (var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("DeepSeekHarnessDesktop.WindowsPnpmWorkspace")!)
using (var reader = new StreamReader(stream))
{
    var policy = reader.ReadToEnd();
    Assert(policy.Contains("nodeLinker: hoisted") && policy.Contains("- win32") && policy.Contains("- x64"), "Relocatable Windows-only policy embedded");
    Assert(policy.Contains("strictDepBuilds: true") && !policy.Contains("dangerouslyAllowAllBuilds"), "Reviewed lifecycle allow-list remains enabled");
}

if (args.Length == 0) return;
if (args.Length != 2 || args[0] is not ("--smoke" or "--update" or "--rollback"))
    throw new ArgumentException("Use --smoke, --update, or --rollback <isolated-runtime-root>.");
var runtimeRoot = Path.GetFullPath(args[1]);
// Tests must use a disposable runtime; never access the user's ~/.dsh.
var testHome = Path.Combine(Path.GetTempPath(), "DSH-TestProfiles", Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("DSH_HOME", testHome);
Environment.SetEnvironmentVariable("DSH_DESKTOP_RUNTIME", runtimeRoot);
using var initialManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "current.json")));
var initialVersion = initialManifest.RootElement.GetProperty("version").GetString();
var initialRelease = Path.Combine(runtimeRoot, initialManifest.RootElement.GetProperty("releaseDirectory").GetString()!);
await using var runtime = new HarnessRuntimeManager();
var initialUri = await runtime.StartAsync();
using var http = new HttpClient();
Assert((await http.GetAsync(initialUri)).StatusCode == HttpStatusCode.OK, "Backend serves desktop UI");
Assert((await http.GetAsync(new Uri(initialUri, "/"))).StatusCode == HttpStatusCode.OK, "Session cookie works without the launch token");
if (args[0] != "--smoke")
{
    var startAttempts = 0;
    using var updater = new UpdateService(UpdateServiceOptions.CreateForPortableRuntime(
        runtimeRoot, Path.Combine(runtimeRoot, "node/node.exe"),
        Path.Combine(runtimeRoot, "node/node_modules/npm/bin/npm-cli.js"),
        stopRuntimeAsync: runtime.StopAsync,
        startAndVerifyRuntimeAsync: async (_, token) =>
        {
            startAttempts++;
            if (args[0] == "--rollback" && startAttempts == 1) return false;
            var uri = await runtime.StartAsync(token);
            return (await http.GetAsync(uri, token)).IsSuccessStatusCode;
        },
        pnpmScriptPath: Path.Combine(initialRelease, "node_modules/pnpm/bin/pnpm.cjs")));
    updater.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusMessage") Console.WriteLine(updater.StatusMessage); };
    var check = await updater.CheckForUpdatesAsync();
    Assert(check.IsUpdateAvailable, "An actual newer registry version is available");
    var result = await updater.InstallAvailableUpdateAsync();
    if (args[0] == "--rollback")
        Assert(result.RolledBack && result.CurrentVersion == initialVersion, "Failed health check restores the old version");
    else
        Assert(result.Succeeded && result.CurrentVersion == check.AvailableVersion, "Install, switch and verify newer runtime: " + (result.Error ?? result.CurrentVersion));
    Assert(!File.Exists(Path.Combine(runtimeRoot, "pending-update.json")), "No pending transaction remains");
}
var runningUri = runtime.BaseUri!;
await runtime.StopAsync();
Assert(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
    endpoint.Port == runningUri.Port && IPAddress.IsLoopback(endpoint.Address)), "Backend stops with its owner (TCP listener closed)");
var credentialsFile = Path.Combine(testHome, ".credentials.yaml");
Assert(!File.Exists(credentialsFile) || !Regex.IsMatch(File.ReadAllText(credentialsFile), @"api[-_]?key|sk-", RegexOptions.IgnoreCase), "Fresh profile has no provider API keys (local browser signing credentials are allowed)");
