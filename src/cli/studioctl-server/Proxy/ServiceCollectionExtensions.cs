namespace Altinn.Studio.StudioctlServer.Proxy;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ProxyOptions.FromConfiguration(configuration));
        services.AddHttpForwarder();
        services.AddSingleton<PlatformTransformer>();
        return services;
    }
}
