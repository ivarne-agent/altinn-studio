using System.Threading;
using System.Threading.Tasks;
using Altinn.Studio.Designer.Models;

namespace Altinn.Studio.Designer.Services.Interfaces;

/// <summary>
/// Reads the app library version of an app at a given commit in Gitea, such as the commit a release is built from.
/// </summary>
public interface IGiteaAppVersionService
{
    /// <summary>
    /// Returns the version of the <c>Altinn.App.Api</c> or <c>Altinn.App.Api.Experimental</c> package that
    /// <c>App/App.csproj</c> references at <paramref name="reference"/>. A missing or unreadable project file is
    /// reported in the result rather than thrown.
    /// </summary>
    Task<AppLibVersionLookup> GetAppLibVersionAsync(
        string org,
        string app,
        string reference,
        CancellationToken cancellationToken = default
    );
}
