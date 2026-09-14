using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using SysPulse.App.Native;
using SysPulse.Core;
using SysPulse.Core.Power;
using SysPulse.Core.Rules;
using SysPulse.Core.Settings;

namespace SysPulse.App;

public partial class MainWindow : Window
{
    // Segoe Fluent Icons code points.
    private static readonly string MaximizeGlyph = ((char)0xE922).ToString();
    private static readonly string RestoreGlyph = ((char)0xE923).ToString();

    private readonly ISettingsService _settings;
    private readonly IPowerProfileService _power;
    private readonly INotifier _notifier;
    private bool _showingProfile;
    private bool _trayHintShown;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
        _notifier = services.GetRequiredService<INotifier>();

        _settings = services.GetRequiredService<ISettingsService>();
        ApplyAccent(_settings.Current);
        _settings.Changed += OnSettingsChanged;

        _power = services.GetRequiredService<IPowerProfileService>();
        if (_power.IsSupported)
        {
            ShowCurrentProfile();
            ProfileSelector.SelectionChanged += ProfileSelector_SelectionChanged;
            _power.Changed += OnProfileChanged;

            // Windows' own power settings can change the mode while we're in the background.
            Activated += (_, _) => ShowCurrentProfile();
        }
        else
        {
            ProfileSelector.IsEnabled = false;
            ProfileSelector.ToolTip = "Windows power modes aren't available on this PC.";
        }
    }

    /// <summary>Creates the window handle without showing the window, for starting in the tray.</summary>
    public void StartHidden() => new WindowInteropHelper(this).EnsureHandle();

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source.AddHook(WndProc);

        // Without this, Windows blocks these messages from a non-elevated sender to an elevated copy.
        NativeMethods.ChangeWindowMessageFilterEx(source.Handle, App.ActivateMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
        NativeMethods.ChangeWindowMessageFilterEx(source.Handle, App.QuitMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == App.ActivateMessage)
        {
            BringToFront();
            handled = true;
        }
        else if ((uint)msg == App.QuitMessage)
        {
            // Queue it: shutting down from inside the window procedure would tear the window down mid-message.
            Dispatcher.InvokeAsync(App.Quit);
            handled = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (App.IsExiting || !_settings.Current.CloseToTray)
            return;

        e.Cancel = true;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _notifier.Notify("SysPulse is still running", "It's in the tray, keeping rules and the Flight Recorder going. Right-click the tray icon to exit.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        _power.Changed -= OnProfileChanged;
        base.OnClosed(e);

        // The app uses explicit shutdown, so a real close (Close to tray off) has to end it.
        if (!App.IsExiting)
            App.Quit();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        var maximized = WindowState == WindowState.Maximized;

        // A maximized WindowChrome window overhangs the screen by its resize border; pad the content back into view.
        var border = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);

        MaximizeButton.Content = maximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnSettingsChanged(AppSettings settings) => Dispatcher.InvokeAsync(() => ApplyAccent(settings));

    private void ApplyAccent(AppSettings settings)
    {
        var color = AccentPalette.For(settings.Accent);
        PowerLamp.Background = new SolidColorBrush(color);
        PowerLampGlow.Color = color;
    }

    // Rules switch profiles from a background thread.
    private void OnProfileChanged(PowerProfile profile) => Dispatcher.InvokeAsync(ShowCurrentProfile);

    private void ShowCurrentProfile()
    {
        _showingProfile = true;
        try
        {
            // Items are in PowerProfile order. A custom power plan shows as no selection.
            ProfileSelector.SelectedIndex = _power.Current is { } profile ? (int)profile : -1;
        }
        catch (Exception)
        {
            ProfileSelector.SelectedIndex = -1;
        }
        finally
        {
            _showingProfile = false;
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingProfile || ProfileSelector.SelectedIndex < 0)
            return;

        try
        {
            _power.Apply((PowerProfile)ProfileSelector.SelectedIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't change the power mode.\n\n{ex.Message}", "SysPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowCurrentProfile();
        }
    }

    private void WebView_Initializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        // WebView2 defaults to a profile folder next to the exe, which fails if the app lives somewhere
        // read-only like Program Files. Elevated and non-elevated instances also can't share a profile.
        var profile = Elevation.IsElevated() ? "WebView2-Admin" : "WebView2";
        e.UserDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysPulse", profile);
    }

    private void WebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        var settings = e.WebView.CoreWebView2.Settings;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
