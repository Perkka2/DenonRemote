namespace DenonRemote.Services;

/// <summary>
/// Everything the app writes goes under the user's profile, never next to the
/// executable - a published build often lives somewhere read-only.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".denonremote");

    public static string Receivers => Path.Combine(Root, "receivers.json");
    public static string Scenes => Path.Combine(Root, "scenes.json");
    public static string Discovery => Path.Combine(Root, "discovery");

    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
