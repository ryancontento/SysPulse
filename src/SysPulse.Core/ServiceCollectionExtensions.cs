using Microsoft.Extensions.DependencyInjection;
using SysPulse.Core.Services;

namespace SysPulse.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSysPulseMetrics(this IServiceCollection services)
    {
        services.AddSingleton<MetricsService>();
        services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<MetricsService>());
        services.AddHostedService(sp => sp.GetRequiredService<MetricsService>());
        return services;
    }
}
