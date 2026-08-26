using System.Drawing;
using Forms = System.Windows.Forms;

namespace DeepSeekHarnessDesktop;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Action _restoreWindow;
    private readonly Func<Task> _exitApplication;
    private int _hintShown;
    private bool _disposed;

    public TrayIconService(Action restoreWindow, Func<Task> exitApplication)
    {
        _restoreWindow = restoreWindow;
        _exitApplication = exitApplication;
        _icon = LoadApplicationIcon();

        var openItem = new Forms.ToolStripMenuItem("打开 DeepSeek Harness");
        openItem.Click += (_, _) => _restoreWindow();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += async (_, _) => await ExitSafelyAsync();

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "DeepSeek Harness",
            Icon = _icon,
            ContextMenuStrip = contextMenu,
            Visible = true
        };
        _notifyIcon.Click += NotifyIcon_OnClick;
        _notifyIcon.DoubleClick += (_, _) => _restoreWindow();
    }

    private void NotifyIcon_OnClick(object? sender, EventArgs e)
    {
        if (e is Forms.MouseEventArgs { Button: Forms.MouseButtons.Left })
        {
            _restoreWindow();
        }
    }

    public void ShowMinimizedHint()
    {
        if (Interlocked.Exchange(ref _hintShown, 1) != 0 || _disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "DeepSeek Harness 仍在运行";
        _notifyIcon.BalloonTipText = "单击托盘图标可恢复窗口，右键选择“退出”才会关闭后台服务。";
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private async Task ExitSafelyAsync()
    {
        try
        {
            await _exitApplication();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Application exit failed: {exception}");
        }
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var extractedIcon = Icon.ExtractAssociatedIcon(executablePath);
            if (extractedIcon is not null)
            {
                return (Icon)extractedIcon.Clone();
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
