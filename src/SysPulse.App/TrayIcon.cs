using System.Windows;
using SysPulse.Core.Rules;
using SysPulse.Core.Settings;
using Forms = System.Windows.Forms;

namespace SysPulse.App;

/// <summary>
/// The notification-area icon. Clicking it brings the window forward, and rule notifications show as
/// its balloon tips, which Windows 10 and 11 display as regular notifications.
/// </summary>
internal sealed class TrayIcon : INotifier, IDisposable
{
    // Windows truncates balloon text past this.
    private const int MaxMessageLength = 255;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();

    public TrayIcon(ISettingsService settings)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SysPulse.ico")).Stream;

        var widgetItem = new Forms.ToolStripMenuItem("Mini widget");
        widgetItem.Click += (_, _) => settings.Update(s => s with { ShowWidget = !s.ShowWidget });

        _menu.Items.Add("Open SysPulse", null, (_, _) => ShowMainWindow());
        _menu.Items.Add(widgetItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Opening += (_, _) => widgetItem.Checked = settings.Current.ShowWidget;
        _menu.Items.Add("Exit", null, (_, _) => App.Quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "SysPulse",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) => ShowMainWindow();
    }

    /// <summary>
    /// Windows silently drops every app's notifications when the main switch in Settings → System → Notifications
    /// is off. Do Not Disturb also hides them, but it isn't exposed through a public API, so it can't be detected here.
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            return key?.GetValue("ToastEnabled") is 0
                ? "Windows notifications are turned off, so alerts won't pop up. They're still recorded in the activity log below."
                : null;
        }
    }

    /// <summary>Safe to call from any thread.</summary>
    public void Notify(string title, string message)
    {
        if (message.Length > MaxMessageLength)
            message = message[..(MaxMessageLength - 1)] + "…";

        Application.Current?.Dispatcher.InvokeAsync(() => _icon.ShowBalloonTip(10_000, title, message, Forms.ToolTipIcon.None));
    }

    private static void ShowMainWindow() => (Application.Current?.MainWindow as MainWindow)?.BringToFront();

    public void Dispose()
    {
        // Hide before disposing, or the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
