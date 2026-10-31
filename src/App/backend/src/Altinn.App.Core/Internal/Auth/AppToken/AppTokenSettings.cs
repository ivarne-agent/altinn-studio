namespace Altinn.App.Core.Internal.Auth.AppToken;

/// <summary>
/// The app's token for Storage, as the runtime operator provisions it: a short-lived JWT the operator issues for
/// this app and rewrites well before it expires. Bound from the <c>AppToken</c> object of the provisioned file,
/// and only from there; see <see cref="ProvisionedSecrets.ProvisionedSecretFiles.AppToken"/>.
/// </summary>
internal sealed class AppTokenSettings
{
    /// <summary>
    /// The signed token, sent as it is.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// When the operator issued the token.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// When the token expires. The app stops sending it then, rather than have Storage reject it.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// When the operator intends to rewrite the file. The app does not act on it: it reads the file on every
    /// request, so a rewrite reaches it without being asked for.
    /// </summary>
    public DateTimeOffset? RefreshAt { get; set; }
}
