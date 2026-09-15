using System;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LanSync.Tray;

public partial class App : Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();
        _notifyIcon = CreateNotifyIcon();
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        var icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "LanSync",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => ShowMainWindow();
        return icon;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}