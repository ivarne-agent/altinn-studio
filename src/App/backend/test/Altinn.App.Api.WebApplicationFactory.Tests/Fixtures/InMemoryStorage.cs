using Altinn.App.Core.Infrastructure.Clients.Storage;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Internal.Data;
using Altinn.App.Core.Internal.Instances;
using Altinn.App.Tests.Common.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// Points the app's own <see cref="InstanceClient"/> and <see cref="DataClient"/> at an in-memory
/// <see cref="StorageClientInterceptor"/>, so that the real client code runs against a fake of the Storage API
/// and nothing touches the file system. One interceptor per host, built from the application metadata the host
/// loaded (after the app files hooks ran), and reset between tests by the fixture.
/// </summary>
internal static class InMemoryStorage
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new StorageClientInterceptor(
            sp.GetRequiredService<IAppMetadata>().ApplicationMetadata
        ));
        services
            .AddHttpClient<IDataClient, DataClient>()
            .ConfigurePrimaryHttpMessageHandler(sp => new ForwardingHandler(
                sp.GetRequiredService<StorageClientInterceptor>()
            ));
        services
            .AddHttpClient<IInstanceClient, InstanceClient>()
            .ConfigurePrimaryHttpMessageHandler(sp => new ForwardingHandler(
                sp.GetRequiredService<StorageClientInterceptor>()
            ));
    }

    /// <summary>
    /// The primary handler the client factory owns and disposes with its handler chain; the interceptor itself
    /// is the host's singleton and outlives every chain.
    /// </summary>
    private sealed class ForwardingHandler(StorageClientInterceptor storage) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(storage, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
