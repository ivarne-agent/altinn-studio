using System;
using System.Diagnostics.CodeAnalysis;
using NuGet.Versioning;

namespace Altinn.Studio.Designer.Models;

public enum AppLibVersionLookupStatus
{
    Found,
    CsprojFetchFailed,
    CsprojNotFound,
    CsprojDecodeFailed,
    CsprojParseFailed,
    PackageReferenceNotFound,
}

/// <summary>
/// The app library version of an app, or why it could not be determined.
/// </summary>
public sealed class AppLibVersionLookup
{
    private AppLibVersionLookup(AppLibVersionLookupStatus status, SemanticVersion? version, Exception? exception)
    {
        Status = status;
        Version = version;
        Exception = exception;
    }

    public AppLibVersionLookupStatus Status { get; }

    /// <summary>
    /// The version, set only when <see cref="Status"/> is <see cref="AppLibVersionLookupStatus.Found"/>.
    /// </summary>
    public SemanticVersion? Version { get; }

    /// <summary>
    /// The exception that made the lookup fail, if any.
    /// </summary>
    public Exception? Exception { get; }

    [MemberNotNullWhen(true, nameof(Version))]
    public bool IsFound => Status == AppLibVersionLookupStatus.Found;

    public static AppLibVersionLookup Found(SemanticVersion version) =>
        new(AppLibVersionLookupStatus.Found, version, null);

    public static AppLibVersionLookup Failed(AppLibVersionLookupStatus status, Exception? exception = null)
    {
        if (status == AppLibVersionLookupStatus.Found)
        {
            throw new ArgumentException("A failed lookup needs a failure status.", nameof(status));
        }

        return new(status, null, exception);
    }
}
