using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using SysPulse.Core;

namespace SysPulse.App;

public partial class MainWindow : Window
{
    // Segoe Fluent Icons code points.
    private static readonly string MaximizeGlyph = ((char)0xE922).ToString();
    private static readonly string RestoreGlyph = ((char)0xE923).ToString();

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
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
#if !DEBUG
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
