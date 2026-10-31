namespace Altinn.App.Core.Internal.Auth.AppToken;

/// <summary>
/// Sends the app's token to Storage on every request of the client it is attached to, in the
/// <see cref="HeaderName"/> header. A request goes out without the header when there is no usable token;
/// <see cref="AppTokenProvider"/> reports that, and Storage decides what it means.
/// </summary>
internal sealed class AppTokenDelegatingHandler(AppTokenProvider tokenProvider) : DelegatingHandler
{
    /// <summary>
    /// The header Storage reads the app token from.
    /// </summary>
    internal const string HeaderName = "Altinn-App-Token";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // A retried request passes through here again, and must carry the token current now, once.
        request.Headers.Remove(HeaderName);

        if (tokenProvider.GetToken() is { } token)
        {
            request.Headers.TryAddWithoutValidation(HeaderName, token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
