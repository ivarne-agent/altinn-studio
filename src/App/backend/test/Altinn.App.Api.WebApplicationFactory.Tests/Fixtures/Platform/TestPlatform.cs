namespace Altinn.App.Api.Tests.Fixtures.Platform;

/// <summary>
/// What stands in for the Altinn platform behind the app under test.
/// </summary>
public enum TestPlatform
{
    /// <summary>In-memory mocks of the platform clients, registered by the test host.</summary>
    InMemory,

    /// <summary>A running localtest, reached by the real clients from Altinn.App.Core. Not implemented yet.</summary>
    Localtest,
}

/// <summary>
/// Resolves the platform the tests run against once per process, from the environment variable
/// <c>ALTINN_APP_API_TESTS_PLATFORM</c> (<c>InMemory</c>, the default, or <c>Localtest</c>).
/// </summary>
public static class TestPlatformSelection
{
    public const string EnvironmentVariable = "ALTINN_APP_API_TESTS_PLATFORM";

    public static TestPlatform Current { get; } = Resolve();

    private static readonly Lazy<IAppTestPlatform> _platform = new(
        Create,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    /// <summary>
    /// The platform's test-side operations. One per process: the test host reads it while it is built, and
    /// every fixture hands it to its tests.
    /// </summary>
    public static IAppTestPlatform Platform => _platform.Value;

    private static TestPlatform Resolve()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return TestPlatform.InMemory;
        if (Enum.TryParse(value, ignoreCase: true, out TestPlatform platform) && Enum.IsDefined(platform))
            return platform;
        throw new InvalidOperationException(
            $"{EnvironmentVariable}={value} is not a test platform. Use one of: {string.Join(", ", Enum.GetNames<TestPlatform>())}."
        );
    }

    private static IAppTestPlatform Create() =>
        Current switch
        {
            TestPlatform.InMemory => new InMemoryTestPlatform(),
            TestPlatform.Localtest => throw new NotSupportedException(
                $"{nameof(TestPlatform.Localtest)} is not implemented. It needs an {nameof(IAppTestPlatform)} that "
                    + "fetches tokens from localtest and prepares instances through its API, the localtest branch of "
                    + "ConfigurePlatformServices in Program.cs, and a host that listens on a real port "
                    + $"({nameof(AppApiFixture)}.ConfigureWebHost)."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(Current), Current, "Unknown test platform"),
        };
}
