using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KeyPulse.Configuration;
using Serilog;

namespace KeyPulse.Services;

public sealed class TrayIconService(UpdateService updateService) : IDisposable
{
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _updateMenuItem;

    public void Initialize(Action showMainWindow, Action shutdown)
    {
        if (_trayIcon != null)
            return;

        var trayIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.Tray.TrayIconRelativePath);
        _trayIcon = new NotifyIcon
        {
            Icon = new Icon(trayIconPath),
            Visible = true,
            Text = AppConstants.App.DefaultName,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _updateMenuItem = new ToolStripMenuItem("No updates available") { Enabled = false };
        _updateMenuItem.Click += (_, _) => updateService.InstallUpdate();
        _trayIcon.ContextMenuStrip.Items.Add(_updateMenuItem);

        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => shutdown());
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                showMainWindow();
        };

        updateService.UpdateStatusChanged += OnUpdateStatusChanged;
        UpdateUpdateMenuItem(updateService.UpdateAvailable, updateService.LatestVersion);

        Log.Information("Tray icon initialized");
    }

    private void OnUpdateStatusChanged(UpdateService.UpdateAvailableEventArgs args)
    {
        UpdateUpdateMenuItem(args.Available, args.LatestVersion);
    }

    private void UpdateUpdateMenuItem(bool available, string? latestVersion)
    {
        if (_updateMenuItem == null)
            return;

        void Apply()
        {
            if (available)
            {
                _updateMenuItem.Text = $"Install update (v{latestVersion})";
                _updateMenuItem.Enabled = true;
            }
            else
            {
                _updateMenuItem.Text = "No updates available";
                _updateMenuItem.Enabled = false;
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.BeginInvoke(new Action(Apply));
    }

    public void ShowWarning(string title, string message, int timeoutMs)
    {
        if (_trayIcon == null)
            return;

        _trayIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        updateService.UpdateStatusChanged -= OnUpdateStatusChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
