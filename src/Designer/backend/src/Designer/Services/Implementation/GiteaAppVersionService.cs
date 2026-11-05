using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Altinn.Studio.Designer.Clients.Interfaces;
using Altinn.Studio.Designer.Helpers;
using Altinn.Studio.Designer.Models;
using Altinn.Studio.Designer.Services.Interfaces;
using NuGet.Versioning;

namespace Altinn.Studio.Designer.Services.Implementation;

public class GiteaAppVersionService(IGiteaClient giteaClient) : IGiteaAppVersionService
{
    private const string AppCsprojPath = "App/App.csproj";
    private static readonly string[] s_appLibPackageNames = ["Altinn.App.Api", "Altinn.App.Api.Experimental"];

    /// <inheritdoc />
    public async Task<AppLibVersionLookup> GetAppLibVersionAsync(
        string org,
        string app,
        string reference,
        CancellationToken cancellationToken = default
    )
    {
        FileSystemObject? appCsproj;
        try
        {
            appCsproj = await giteaClient.GetFileAsync(org, app, AppCsprojPath, reference, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AppLibVersionLookup.Failed(AppLibVersionLookupStatus.CsprojFetchFailed, ex);
        }

        if (appCsproj?.Content is null)
        {
            return AppLibVersionLookup.Failed(AppLibVersionLookupStatus.CsprojNotFound);
        }

        byte[] csprojContent;
        try
        {
            csprojContent = Convert.FromBase64String(appCsproj.Content);
        }
        catch (FormatException ex)
        {
            return AppLibVersionLookup.Failed(AppLibVersionLookupStatus.CsprojDecodeFailed, ex);
        }

        try
        {
            return PackageVersionHelper.TryGetPackageVersionFromCsprojContent(
                csprojContent,
                s_appLibPackageNames,
                out SemanticVersion version
            )
                ? AppLibVersionLookup.Found(version)
                : AppLibVersionLookup.Failed(AppLibVersionLookupStatus.PackageReferenceNotFound);
        }
        catch (XmlException ex)
        {
            return AppLibVersionLookup.Failed(AppLibVersionLookupStatus.CsprojParseFailed, ex);
        }
    }
}
