namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// The app folders under <c>TestData/apps</c>, as copied to the build output.
/// </summary>
public static class TestApps
{
    public static string Root => Path.Join(AppContext.BaseDirectory, "TestData", "apps");

    /// <summary>The app every host boots with; the fixture replaces it before the first request.</summary>
    public static string Base => Directory(nameof(Base).ToLowerInvariant());

    /// <summary>The folder of an app checked in under <c>TestData/apps</c>, for <see cref="AppFiles.AppFilesBuilder.FromDirectory"/>.</summary>
    public static string Directory(string name)
    {
        string path = Path.Join(Root, name);
        if (!System.IO.Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"No test app at {path}. Add it under TestData/apps.");
        }
        return path;
    }
}
