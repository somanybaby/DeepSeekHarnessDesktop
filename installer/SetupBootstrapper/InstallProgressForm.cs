using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace DeepSeekHarnessDesktopSetup;

internal sealed class InstallProgressForm : Form
{
    private readonly Func<Action<SetupProgress>, bool> _install;
    private readonly Func<string?> _getLogPath;
    private readonly string? _testOutput;
    private readonly Label _heading = new();
    private readonly Label _stage = new();
    private readonly Label _details = new();
    private readonly Label _percent = new();
    private readonly Label _elapsed = new();
    private readonly Label _hint = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _close = new();
    private readonly Button _logs = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Stopwatch _clock = new();
    private bool _busy = true;
    private int _uiTicks;
    private int _reports;
    private bool _capturedProgress;
    private bool _sawCopy;

    internal InstallProgressForm(Func<Action<SetupProgress>, bool> install, Func<string?> getLogPath, string? testOutput = null)
    {
        _install = install;
        _getLogPath = getLogPath;
        _testOutput = testOutput;
        Text = "DeepSeek Harness Desktop 安装程序";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(680, 440);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 253);
        if (_testOutput is not null) { Opacity = 0; ShowInTaskbar = false; }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30, 25, 30, 22), ColumnCount = 1, RowCount = 9 };
        foreach (var height in new float[] { 45, 30, 46, 30, 52, 26, 48, 32, 48 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _heading.Text = "正在安装 DeepSeek Harness Desktop";
        _heading.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
        _heading.ForeColor = Color.FromArgb(25, 42, 70);
        _heading.Dock = DockStyle.Fill;
        _heading.AutoSize = false;
        layout.Controls.Add(_heading);
        layout.Controls.Add(new Label { Text = "Windows x64 · 安装器 1.0.3 · 完整离线安装", Dock = DockStyle.Fill, ForeColor = Color.DimGray });
        _stage.Text = "准备安装…";
        _stage.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _stage.Dock = DockStyle.Fill;
        _stage.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_stage);
        _bar.Dock = DockStyle.Fill;
        _bar.Margin = new Padding(0, 2, 0, 5);
        _bar.Style = ProgressBarStyle.Continuous;
        layout.Controls.Add(_bar);
        _details.Text = "窗口已就绪，即将检查安装环境。";
        _details.Dock = DockStyle.Fill;
        _details.AutoEllipsis = true;
        _details.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(_details);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _percent.Text = "总进度 0%";
        _percent.Dock = DockStyle.Fill;
        _elapsed.Text = "已用时 00:00";
        _elapsed.Dock = DockStyle.Fill;
        _elapsed.TextAlign = ContentAlignment.TopRight;
        metrics.Controls.Add(_percent);
        metrics.Controls.Add(_elapsed);
        layout.Controls.Add(metrics);
        _hint.Text = "安装期间请勿重复运行安装包。你可以最小化此窗口。\n现有 API 配置、聊天记录和插件会保留。";
        _hint.ForeColor = Color.DimGray;
        _hint.Dock = DockStyle.Fill;
        layout.Controls.Add(_hint);
        layout.Controls.Add(new Label { Text = "总进度按安装阶段计算；数据量为实际处理量。", ForeColor = Color.Gray, Dock = DockStyle.Fill });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _close.Text = "安装中…";
        _close.Enabled = false;
        _close.Size = new Size(112, 34);
        _close.Click += (_, _) => Close();
        _logs.Text = "查看日志";
        _logs.Enabled = false;
        _logs.Size = new Size(112, 34);
        _logs.Click += (_, _) => OpenLog();
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_logs);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        _timer.Tick += (_, _) =>
        {
            if (_busy) _uiTicks++;
            _elapsed.Text = $"已用时 {(int)_clock.Elapsed.TotalMinutes:00}:{_clock.Elapsed.Seconds:00}";
            _logs.Enabled = File.Exists(_getLogPath());
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _clock.Start();
        _timer.Start();
        SaveTestFrame("initial.png");
        // A dedicated STA worker keeps the UI responsive and preserves the COM
        // apartment required by Windows desktop shortcut creation.
        var worker = new Thread(() =>
        {
            try
            {
                var launched = _install(progress => BeginInvoke(new Action(() => ApplyProgress(progress))));
                BeginInvoke(new Action(() => Finish(launched, null)));
            }
            catch (Exception error) { BeginInvoke(new Action(() => Finish(false, error))); }
        })
        { IsBackground = true, Name = "Offline setup worker" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void ApplyProgress(SetupProgress progress)
    {
        if (!_busy) return;
        _reports++;
        _bar.Value = Math.Clamp(progress.Percent, 0, 100);
        _percent.Text = $"总进度 {progress.Percent}%";
        _stage.Text = progress.Stage;
        _details.Text = progress.Detail;
        if (progress.Stage.Contains("复制")) _sawCopy = true;
        if (!_capturedProgress && progress.Percent is >= 10 and <= 55)
        {
            SaveTestFrame("progress.png");
            _capturedProgress = true;
        }
    }

    private void Finish(bool launched, Exception? error)
    {
        _busy = false;
        _timer.Stop();
        _clock.Stop();
        _logs.Enabled = File.Exists(_getLogPath());
        _close.Enabled = true;
        _close.Text = error is null ? "完成" : "关闭";
        if (error is null)
        {
            _heading.Text = "安装完成";
            _stage.Text = launched ? "程序已启动，可以开始使用了。" : "请双击桌面快捷方式打开程序。";
            _details.Text = "首次安装的 API 配置为空，请在软件底部的“API 配置”中添加。";
            _hint.Text = "已有用户的 API 配置、聊天记录和插件保持不变。";
            _bar.Value = 100;
            _percent.Text = "总进度 100%";
        }
        else
        {
            Environment.ExitCode = 1;
            _heading.Text = "安装未完成";
            _heading.ForeColor = Color.Firebrick;
            _stage.Text = "请查看以下原因或打开安装日志。";
            _details.Text = error.Message;
            _hint.Text = "无需删除你的 API 配置或聊天记录。请根据提示处理后重试。";
        }
        if (_testOutput is not null)
        {
            SaveTestFrame(error is null ? "complete.png" : "error.png");
            File.WriteAllText(Path.Combine(_testOutput, "ui-test.json"), JsonSerializer.Serialize(new
            {
                succeeded = error is null,
                uiTicks = _uiTicks,
                progressReports = _reports,
                capturedProgress = _capturedProgress,
                sawCopy = _sawCopy,
                finalPercent = _bar.Value,
                closeEnabled = _close.Enabled,
                logEnabled = _logs.Enabled,
                error = error?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (error is null && (_uiTicks < 3 || _reports < 5 || !_capturedProgress || !_sawCopy)) Environment.ExitCode = 2;
            Close();
        }
    }

    private void SaveTestFrame(string name)
    {
        if (_testOutput is null) return;
        Directory.CreateDirectory(_testOutput);
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.Save(Path.Combine(_testOutput, name), System.Drawing.Imaging.ImageFormat.Png);
    }

    private void OpenLog()
    {
        var path = _getLogPath();
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { _details.Text = "无法打开日志：" + error.Message; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
