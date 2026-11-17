using Altinn.App.Api.Tests.Fixtures.Platform;
using Altinn.App.Core.Features;
using Altinn.App.Core.Internal.Profile;
using Altinn.App.Core.Internal.Registers;
using Altinn.Platform.Profile.Models;
using Altinn.Platform.Register.Models;

namespace Altinn.App.Api.Tests.Mocks;

/// <summary>The register answered from <see cref="TestPlatformData.Parties"/>. An unknown party is an error, as a missing fixture was.</summary>
public sealed class InMemoryRegisterClient(TestPlatformData data) : IRegisterClient
{
    public async Task<Party?> GetPartyUnchecked(
        int partyId,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    )
    {
        var parties = await GetPartyListUnchecked([partyId], authenticationMethod, cancellationToken);
        return parties.SingleOrDefault(p => p.PartyId == partyId);
    }

    public Task<IReadOnlyList<Party>> GetPartyListUnchecked(
        IReadOnlyList<int> partyIds,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<Party> parties = partyIds
            .Select(id =>
                data.Parties.TryGetValue(id, out Party? party)
                    ? party
                    : throw new KeyNotFoundException(
                        $"No party {id} in the test platform data; add it with {nameof(TestPlatformData)}.{nameof(TestPlatformData.AddParty)}"
                    )
            )
            .ToList();
        return Task.FromResult(parties);
    }
}

/// <summary>The profile API answered from <see cref="TestPlatformData.Users"/>.</summary>
public sealed class InMemoryProfileClient(TestPlatformData data) : IProfileClient
{
    public Task<UserProfile?> GetUserProfile(
        int userId,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(data.Users.GetValueOrDefault(userId));

    public Task<UserProfile?> GetUserProfile(
        string ssn,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(data.Users.Values.FirstOrDefault(u => u.Party?.SSN == ssn));

    public Task<UserProfile?> GetUserProfile(Guid userUuid, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Users.Values.FirstOrDefault(u => u.UserUuid == userUuid));
}

/// <summary>The party lookups of the register answered from <see cref="TestPlatformData.Parties"/>.</summary>
public sealed class InMemoryPartyClient(TestPlatformData data) : IAltinnPartyClient
{
    public Task<Party?> GetParty(
        int partyId,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(data.Parties.GetValueOrDefault(partyId));

    public Task<Party> LookupParty(
        PartyLookup partyLookup,
        StorageAuthenticationMethod? authenticationMethod = null,
        CancellationToken cancellationToken = default
    )
    {
        Party? party = data.Parties.Values.FirstOrDefault(p =>
            (partyLookup.OrgNo is not null && p.OrgNumber == partyLookup.OrgNo)
            || (partyLookup.Ssn is not null && p.SSN == partyLookup.Ssn)
        );
        return Task.FromResult(
            party
                ?? throw new KeyNotFoundException(
                    $"No party with orgNo {partyLookup.OrgNo} or ssn {partyLookup.Ssn} in the test platform data"
                )
        );
    }

    public Task<int?> GetPartyIdByUrn(string urn, CancellationToken cancellationToken = default) =>
        Task.FromResult(FindByUrn(urn)?.PartyId);

    public Task<Guid?> GetPartyUuidByUrn(string urn, CancellationToken cancellationToken = default) =>
        Task.FromResult(FindByUrn(urn)?.PartyUuid);

    private Party? FindByUrn(string urn)
    {
        int separator = urn.LastIndexOf(':');
        if (separator < 0)
        {
            return null;
        }
        string type = urn[..separator];
        string value = urn[(separator + 1)..];
        return type switch
        {
            Altinn.App.Core.Constants.AltinnUrns.OrganizationNumber
            or Altinn.App.Core.Constants.AltinnUrns.LegacyOrganizationNumber => data.Parties.Values.FirstOrDefault(p =>
                p.OrgNumber == value
            ),
            Altinn.App.Core.Constants.AltinnUrns.PersonId => data.Parties.Values.FirstOrDefault(p => p.SSN == value),
            Altinn.App.Core.Constants.AltinnUrns.PartyId => data.Parties.GetValueOrDefault(int.Parse(value)),
            _ => throw new NotImplementedException($"{nameof(InMemoryPartyClient)} does not resolve {type} urns"),
        };
    }
}
