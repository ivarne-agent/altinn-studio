using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.Platform.Profile.Models;
using Altinn.Platform.Register.Enums;
using Altinn.Platform.Register.Models;
using Authorization.Platform.Authorization.Models;

namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// The parties, users and roles the in-memory platform fakes answer from. Each fixture owns one, started from
/// <see cref="LoadDefaults"/>, and a test class adds what it needs through the <c>Add</c> methods.
/// </summary>
/// <remarks>
/// The defaults are the users and parties the API tests have always used: user 1337 (Sophie Salt, party 501337)
/// who represents the organisations 500600, 500000 and their sub units, users 1001 to 1003, and the test
/// service owner. They are read from <c>TestData/platform</c>.
/// </remarks>
public sealed class TestPlatformData
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ConcurrentDictionary<int, Party> Parties { get; } = new();

    public ConcurrentDictionary<int, UserProfile> Users { get; } = new();

    /// <summary>The roles a user holds for a party, by (user id, party id).</summary>
    public ConcurrentDictionary<(int UserId, int PartyId), List<Role>> Roles { get; } = new();

    public static string Root => Path.Join(AppContext.BaseDirectory, "TestData", "platform");

    public static TestPlatformData LoadDefaults()
    {
        var data = new TestPlatformData();
        foreach (string file in Directory.EnumerateFiles(Path.Join(Root, "parties"), "*.json"))
        {
            data.AddParty(Read<Party>(file));
        }
        foreach (string file in Directory.EnumerateFiles(Path.Join(Root, "users"), "*.json"))
        {
            data.AddUser(Read<UserProfile>(file));
        }
        foreach (string file in Directory.EnumerateFiles(Path.Join(Root, "roles"), "*.json"))
        {
            // {userId}_{partyId}.json
            string[] ids = Path.GetFileNameWithoutExtension(file).Split('_');
            data.Roles[(int.Parse(ids[0]), int.Parse(ids[1]))] = Read<List<Role>>(file);
        }
        return data;
    }

    public TestPlatformData AddParty(Party party)
    {
        Parties[party.PartyId] = party;
        return this;
    }

    public TestPlatformData AddPerson(int partyId, string ssn, string name) =>
        AddParty(
            new Party
            {
                PartyId = partyId,
                PartyTypeName = PartyType.Person,
                SSN = ssn,
                Name = name,
                Person = new Person { SSN = ssn, Name = name },
            }
        );

    public TestPlatformData AddOrganisation(int partyId, string orgNumber, string name) =>
        AddParty(
            new Party
            {
                PartyId = partyId,
                PartyTypeName = PartyType.Organisation,
                OrgNumber = orgNumber,
                Name = name,
                Organization = new Organization { OrgNumber = orgNumber, Name = name },
            }
        );

    public TestPlatformData AddUser(UserProfile user)
    {
        Users[user.UserId] = user;
        return this;
    }

    /// <summary>Gives <paramref name="userId"/> the roles for <paramref name="partyId"/>, replacing any it had.</summary>
    public TestPlatformData AddRoles(int userId, int partyId, params string[] roleCodes)
    {
        Roles[(userId, partyId)] = roleCodes.Select(code => new Role { Type = "altinn", Value = code }).ToList();
        return this;
    }

    public List<Role> RolesFor(int userId, int partyId) =>
        Roles.TryGetValue((userId, partyId), out List<Role>? roles) ? [.. roles] : [];

    /// <summary>The parties a user can represent: its own, and every party it holds a role for.</summary>
    public List<Party> PartyListFor(int userId)
    {
        var partyIds = new SortedSet<int>();
        if (Users.TryGetValue(userId, out UserProfile? user))
        {
            partyIds.Add(user.PartyId);
        }
        foreach ((int roleUserId, int partyId) in Roles.Keys)
        {
            if (roleUserId == userId)
            {
                partyIds.Add(partyId);
            }
        }
        return partyIds.Select(id => Parties.GetValueOrDefault(id)).OfType<Party>().ToList();
    }

    private static T Read<T>(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<T>(stream, _jsonOptions)
            ?? throw new InvalidDataException($"{file} deserialized to null");
    }
}
