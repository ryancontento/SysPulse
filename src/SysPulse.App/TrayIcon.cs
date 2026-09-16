using System.Globalization;
using System.Windows;
using SysPulse.Core.Models;
using SysPulse.Core.Rules;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;
using Forms = System.Windows.Forms;

namespace SysPulse.App;

/// <summary>
/// The notification-area icon. Hovering it shows the current readings, clicking it brings the window
/// forward, and rule notifications show as its balloon tips, which Windows 10 and 11 display as regular
/// notifications.
/// </summary>
internal sealed class TrayIcon : INotifier, IDisposable
{
    // Windows truncates balloon text past this.
    private const int MaxMessageLength = 255;

    // NotifyIcon.Text throws past 63 characters, so the tooltip is built short and clipped as a backstop.
    private const int MaxTooltipLength = 63;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly ISettingsService _settings;
    private readonly IMetricsSource _metrics;
    private bool _disposed;

    public TrayIcon(ISettingsService settings, IMetricsSource metrics)
    {
        _settings = settings;
        _metrics = metrics;

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

        _metrics.Updated += OnMetricsUpdated;
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

    /// <summary>Samples arrive on a background thread; the icon can only be touched on the UI thread.</summary>
    private void OnMetricsUpdated(SystemSnapshot snapshot) =>
        Application.Current?.Dispatcher.InvokeAsync(() => UpdateTooltip(snapshot));

    private void UpdateTooltip(SystemSnapshot snapshot)
    {
        if (_disposed)
            return;

        var text = BuildTooltip(snapshot, _settings.Current.TemperatureUnit);

        // Only write when it actually changes, so hovering doesn't fight a once-a-second rewrite.
        if (text != _icon.Text)
            _icon.Text = text;
    }

    private static string BuildTooltip(SystemSnapshot snapshot, TemperatureUnit unit)
    {
        if (!snapshot.HasData)
            return "SysPulse";

        var text = string.Join(
            Environment.NewLine,
            DeviceLine("CPU", snapshot.Cpu, unit),
            DeviceLine("GPU", snapshot.Gpu, unit),
            $"RAM {Percent(snapshot.Memory.LoadPercent)}");

        return text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;
    }

    /// <summary>"CPU 34% · 72 °C", or just "CPU 34%" when this machine doesn't report the temperature.</summary>
    private static string DeviceLine(string label, DeviceMetrics device, TemperatureUnit unit)
    {
        var load = $"{label} {Percent(device.LoadPercent)}";
        if (device.TemperatureC is not { } celsius)
            return load;

        var fahrenheit = unit == TemperatureUnit.Fahrenheit;
        var degrees = (fahrenheit ? celsius * 9 / 5 + 32 : celsius).ToString("0", CultureInfo.CurrentCulture);
        return $"{load} · {degrees} {(fahrenheit ? "°F" : "°C")}";
    }

    private static string Percent(double value) => value.ToString("0", CultureInfo.CurrentCulture) + "%";

    private static void ShowMainWindow() => (Application.Current?.MainWindow as MainWindow)?.BringToFront();

    /// <summary>Registered both as itself and as <see cref="INotifier"/>, so this can be called more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _metrics.Updated -= OnMetricsUpdated;

        // Hide before disposing, or the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
