using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.App.Native;
using SysPulse.Core;
using SysPulse.Core.Rules;
using SysPulse.Core.Startup;

namespace SysPulse.App;

public partial class App : Application
{
    // "Local\" scopes the mutex to the sign-in session, so different Windows users can each run a copy.
    private const string SingleInstanceMutexName = @"Local\SysPulse.SingleInstance";

    /// <summary>Broadcast by a second launch; the running copy shows its window when it receives it.</summary>
    internal static readonly uint ActivateMessage = NativeMethods.RegisterWindowMessage("SysPulse.Activate");

    /// <summary>Broadcast by tools/Publish.ps1 so a running copy exits cleanly and releases its files.</summary>
    internal static readonly uint QuitMessage = NativeMethods.RegisterWindowMessage("SysPulse.Quit");

    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private WidgetController? _widget;

    /// <summary>True once SysPulse is really quitting, so closing the window no longer just hides it to the tray.</summary>
    internal static bool IsExiting { get; private set; }

    internal static void Quit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatalError(args.ExceptionObject as Exception);

        if (!TryClaimSingleInstance())
        {
            // Two copies would duplicate all the polling and fight over the same ETW session. The running copy may be
            // hidden in the tray (no visible window to find), so ask it to show itself instead.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // The window can be closed to the tray; the app ends through App.Quit (tray menu, or closing with Close to tray off).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => IsExiting = true;

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
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<TrayIcon>());
        builder.Services.AddSysPulseMetrics();

        _host = builder.Build();

        // Create the tray icon on the UI thread before anything can send a notification through it.
        _host.Services.GetRequiredService<TrayIcon>();
        _host.Start();

        var window = new MainWindow(_host.Services);
        MainWindow = window;

        if (e.Args.Contains(WindowsStartupService.MinimizedArgument, StringComparer.OrdinalIgnoreCase))
            window.StartHidden();
        else
            window.Show();

        _widget = new WidgetController(_host.Services);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _widget?.Dispose();

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        IsExiting = true;
        Shutdown(1);
    }

    private static void ShowFatalError(Exception? exception) =>
        MessageBox.Show(
            $"SysPulse ran into a problem and needs to close.\n\n{exception?.Message}",
            "SysPulse",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
