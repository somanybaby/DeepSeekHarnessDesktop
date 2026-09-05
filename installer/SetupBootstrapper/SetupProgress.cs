using System.Diagnostics;

namespace DeepSeekHarnessDesktopSetup;

internal sealed record SetupProgress(string Stage, int Percent, string Detail);

/// <summary>Bound UI traffic during thousands of file events; stage changes are immediate.</summary>
internal sealed class SetupProgressReporter(Action<SetupProgress>? callback)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _lastStage;
    private long _lastReport;
    private int _lastPercent;

    internal void Report(string stage, int percent, string detail, bool force = false)
    {
        percent = Math.Clamp(percent, _lastPercent, 100);
        if (!force && stage == _lastStage && _clock.ElapsedMilliseconds - _lastReport < 100) return;
        _lastStage = stage;
        _lastReport = _clock.ElapsedMilliseconds;
        _lastPercent = percent;
        callback?.Invoke(new SetupProgress(stage, percent, detail));
    }

    internal static string FormatBytes(long value) => value >= 1024L * 1024 * 1024
        ? $"{value / (1024d * 1024 * 1024):0.00} GB"
        : $"{value / (1024d * 1024):0.0} MB";
}
