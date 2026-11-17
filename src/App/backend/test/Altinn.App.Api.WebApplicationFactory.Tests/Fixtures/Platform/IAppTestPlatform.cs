using Altinn.App.Core.Configuration;

namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// What a test needs from the platform behind the app that the app's own clients cannot give it: tokens, and
/// the settings that point the app at the platform. Instances come from <see cref="AppTestFixture.CreateInstance"/>,
/// which seeds the in-memory storage directly or, against localtest, goes through the app's own clients. Tests
/// reach the platform through <see cref="ApiTestBase{TFixture}"/> and <see cref="AppTestFixture"/> only, so that
/// a second implementation changes no test.
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
