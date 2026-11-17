using System.Runtime.CompilerServices;
using Altinn.App.Core.Configuration;

namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// What a test needs from the platform behind the app: tokens, instances and the settings that point the app
/// at it. Tests reach it through <see cref="ApiTestBase{TFixture}"/> and <see cref="AppApiFixture"/> only, so
/// that a second implementation changes no test.
/// </summary>
public interface IAppTestPlatform
{
    TestPlatform Kind { get; }

    /// <summary>The settings the app's platform clients are configured with.</summary>
    PlatformSettings PlatformSettings { get; }

    Task<string> GetUserToken(
        int userId = TestAuthentication.DefaultUserId,
        int partyId = TestAuthentication.DefaultUserPartyId,
        int authenticationLevel = TestAuthentication.DefaultUserAuthenticationLevel
    );

    Task<string> GetServiceOwnerToken(
        string orgNumber = TestAuthentication.DefaultOrgNumber,
        string org = TestAuthentication.DefaultOrg,
        string scope = TestAuthentication.DefaultServiceOwnerScope
    );

    /// <summary>
    /// Resets an instance and its data to the state the test data describes. <paramref name="callerFilePath"/>
    /// identifies the test file that owns the instance; two files must not prepare the same one.
    /// </summary>
    Task PrepareInstance(
        string org,
        string app,
        int instanceOwnerId,
        Guid instanceGuid,
        [CallerFilePath] string callerFilePath = ""
    );

    Task DeleteInstance(string org, string app, int instanceOwnerId, Guid instanceGuid);
}
