using Altinn.Studio.StudioctlServer.Platform;

namespace Altinn.Studio.StudioctlServer.Provisioning;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProvisioningServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddHttpClient(
            AppProvisioner.HttpClientName,
            static client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(StudioctlUserAgent.Value);
            }
        );
        services.AddSingleton(AppProvisionerOptions.FromConfiguration(configuration));
        services.AddSingleton<AppProvisioner>();
        return services;
    }
}
