using Microsoft.Extensions.DependencyInjection;
using SysPulse.Core.Audio;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;
using SysPulse.Core.Specs;

namespace SysPulse.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSysPulseMetrics(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IHardwareSpecsProvider, HardwareSpecsProvider>();
        services.AddSingleton<IAudioService, CoreAudioService>();
        services.AddSingleton<MetricsService>();
        services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<MetricsService>());
        services.AddHostedService(sp => sp.GetRequiredService<MetricsService>());
        return services;
    }
}
