using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.App.Native;
using SysPulse.Core;

namespace SysPulse.App;

public partial class App : Application
{
    // "Local\" scopes the mutex to the sign-in session, so different Windows users can each run a copy.
    private const string SingleInstanceMutexName = @"Local\SysPulse.SingleInstance";

    private IHost? _host;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatalError(args.ExceptionObject as Exception);

        if (!TryClaimSingleInstance())
        {
            // Two copies would duplicate all the polling and fight over the same ETW session.
            ActivateExistingInstance();
            Shutdown();
            return;
        }

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

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool TryClaimSingleInstance()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _singleInstanceMutex = mutex;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated instance owns the mutex, and its default security keeps a non-elevated process from opening it.
            return false;
        }
    }

    private static void ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (process.Id == current.Id || window == IntPtr.Zero)
                    continue;

                if (NativeMethods.IsIconic(window))
                    NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);

                NativeMethods.SetForegroundWindow(window);
                return;
            }
        }
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
