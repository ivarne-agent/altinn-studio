using Altinn.App.Core.Configuration;

namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// What a test needs from the platform behind the app that the app's own clients cannot give it: tokens, and
/// the settings that point the app at the platform. Instances are created and deleted through the host's
/// <c>IInstanceClient</c> and <c>IDataClient</c> instead (<see cref="AppApiFixture.CreateInstance"/>), which are
/// mocks or the real clients depending on <see cref="Kind"/>. Tests reach the platform through
/// <see cref="ApiTestBase{TFixture}"/> and <see cref="AppApiFixture"/> only, so that a second implementation
/// changes no test.
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
}
