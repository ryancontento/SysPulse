using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SysPulse.Core.Settings;

namespace SysPulse.App;

/// <summary>Opens and closes the mini widget to follow <see cref="AppSettings.ShowWidget"/>.</summary>
internal sealed class WidgetController : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private WidgetWindow? _window;

    public WidgetController(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsService>();
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current);
    }

    private void OnSettingsChanged(AppSettings settings) => Application.Current?.Dispatcher.InvokeAsync(() => Apply(settings));

    private void Apply(AppSettings settings)
    {
        if (settings.ShowWidget && _window is null)
        {
            var window = new WidgetWindow(_services);
            window.Closed += (_, _) => _window = null;
            _window = window;
            window.Show();
        }
        else if (!settings.ShowWidget)
        {
            _window?.Close();
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _window?.Close();
    }
}
