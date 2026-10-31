using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Altinn.App.Core.Internal.Auth.AppToken;

internal static class AppTokenHttpClientBuilderExtensions
{
    /// <summary>
    /// Sends the app's token on every request of this client. For the clients that call Storage, which is what
    /// the token is issued for; the token itself is bound in <c>AddPlatformServices</c>.
    /// </summary>
    /// <param name="builder">The client's builder.</param>
    public static IHttpClientBuilder AddAppToken(this IHttpClientBuilder builder)
    {
        builder.Services.TryAddSingleton<AppTokenProvider>();
        builder.Services.TryAddTransient<AppTokenDelegatingHandler>();

        return builder.AddHttpMessageHandler<AppTokenDelegatingHandler>();
    }
}
