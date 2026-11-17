using Altinn.App.Core.Configuration;

namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// The platform as the in-memory mocks model it: tokens signed with the test certificate, and settings that no
/// mock reads.
/// </summary>
internal sealed class InMemoryTestPlatform : IAppTestPlatform
{
    public TestPlatform Kind => TestPlatform.InMemory;

    public PlatformSettings PlatformSettings { get; } =
        new() { ApiAuthorizationEndpoint = "http://localhost:5101/authorization/api/v1/" };

    public Task<string> GetUserToken(int userId, int partyId, int authenticationLevel) =>
        Task.FromResult(TestAuthentication.GetUserToken(userId, partyId, authenticationLevel));

    public Task<string> GetServiceOwnerToken(string orgNumber, string org, string scope) =>
        Task.FromResult(TestAuthentication.GetServiceOwnerToken(orgNumber, org: org, scope: scope));
}
