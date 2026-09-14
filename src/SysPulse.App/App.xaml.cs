using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.Core;

namespace SysPulse.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatalError(args.ExceptionObject as Exception);

        // Paint WebView2 in the page background color so there's no white flash before Blazor renders.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF13120F");

        // An empty builder rooted at the install folder: no appsettings.json, environment-variable config,
        // or console/event-log logging picked up from wherever the exe happens to be launched.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.AddDebug();
        builder.Services.AddWpfBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSysPulseMetrics();

        _host = builder.Build();
        _host.Start();

        MainWindow = new MainWindow(_host.Services);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void ShowFatalError(Exception? exception) =>
        MessageBox.Show(
            $"SysPulse ran into a problem and needs to close.\n\n{exception?.Message}",
            "SysPulse",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
