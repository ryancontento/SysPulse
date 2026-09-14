using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SysPulse.Core.Audio;
using SysPulse.Core.Power;
using SysPulse.Core.Recording;
using SysPulse.Core.Remote;
using SysPulse.Core.Rules;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;
using SysPulse.Core.Specs;
using SysPulse.Core.Startup;

namespace SysPulse.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the monitoring services. Register an <see cref="INotifier"/> first to show rule notifications.</summary>
    public static IServiceCollection AddSysPulseMetrics(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IHardwareSpecsProvider, HardwareSpecsProvider>();
        services.AddSingleton<IAudioService, CoreAudioService>();
        services.AddSingleton<IPowerProfileService, WindowsPowerProfileService>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
        services.TryAddSingleton<INotifier, NullNotifier>();

        services.AddSingleton<MetricsService>();
        services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<MetricsService>());
        services.AddHostedService(sp => sp.GetRequiredService<MetricsService>());

        services.AddSingleton<FlightRecorder>();
        services.AddSingleton<IFlightRecorder>(sp => sp.GetRequiredService<FlightRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<FlightRecorder>());

        services.AddSingleton<RuleEngine>();
        services.AddSingleton<IRuleEngine>(sp => sp.GetRequiredService<RuleEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<RuleEngine>());

        services.AddSingleton<RemoteDashboardServer>();
        services.AddSingleton<IRemoteDashboard>(sp => sp.GetRequiredService<RemoteDashboardServer>());
        services.AddHostedService(sp => sp.GetRequiredService<RemoteDashboardServer>());
        return services;
    }
}
