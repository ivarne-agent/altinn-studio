using System.Security.Claims;
using Altinn.App.Api.Tests.Fixtures.Platform;
using Altinn.App.Core.Constants;
using Altinn.App.Core.Features;
using Altinn.App.Core.Internal.Auth;
using Altinn.App.Core.Models;
using Altinn.Platform.Register.Models;
using Altinn.Platform.Storage.Interface.Models;
using Authorization.Platform.Authorization.Models;
using Microsoft.AspNetCore.Http;

namespace Altinn.App.Api.Tests.Mocks;

/// <summary>
/// The authorization API answered from <see cref="TestPlatformData"/>: party lists and roles. Actions are allowed
/// unless they end in <c>_unauthorized</c>, which is how tests ask for a denial; the policy decisions behind the
/// controllers' authorize attributes come from <see cref="InMemoryPdp"/>.
/// </summary>
public sealed class InMemoryAuthorizationClient(TestPlatformData data, IHttpContextAccessor httpContextAccessor)
    : IAuthorizationClient
{
    public Task<List<Party>?> GetPartyList(
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    )
    {
        // The user of the request, as the platform reads it from the token; the authentication context cannot be
        // injected here because it depends on this client.
        string? userIdClaim = httpContextAccessor.HttpContext?.User.FindFirst(AltinnUrns.UserId)?.Value;
        List<Party> parties = int.TryParse(userIdClaim, out int userId) ? data.PartyListFor(userId) : [];
        return Task.FromResult<List<Party>?>(parties);
    }

    public Task<bool?> ValidateSelectedParty(
        int partyId,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<bool?>(true);

    public Task<bool> AuthorizeAction(
        AppIdentifier appIdentifier,
        InstanceIdentifier instanceIdentifier,
        ClaimsPrincipal user,
        string action,
        string? taskId = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(!action.EndsWith("_unauthorized", StringComparison.Ordinal));

    public Task<Dictionary<string, bool>> AuthorizeActions(
        Instance instance,
        ClaimsPrincipal user,
        List<string> actions,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            actions.ToDictionary(
                action => action,
                action => !action.EndsWith("_unauthorized", StringComparison.Ordinal)
            )
        );

    public Task<List<Role>> GetRoles(int userId, int partyId) => Task.FromResult(data.RolesFor(userId, partyId));

    public Task<List<string>> GetKeyRoleOrganizationParties(
        int userId,
        List<string> orgNumbers,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();
}
