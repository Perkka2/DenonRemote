namespace DenonRemote.Services;

/// <summary>
/// Everything the app writes goes under the user's profile, never next to the
/// executable - a published build often lives somewhere read-only.
///
/// A server or a container has no profile worth the name, so the folder can be
/// named instead: --data on the command line, or DENONREMOTE_DATA where there is
/// no command line to speak of.
/// </summary>
public static class AppPaths
{
    public const string DataVariable = "DENONREMOTE_DATA";

    /// <summary>Set once, by the command line, before anything is read or written.</summary>
    public static string Root { get; set; } = Default(Environment.GetEnvironmentVariable(DataVariable));

    /// <summary>Internal so the self-test can ask without changing the environment.</summary>
    internal static string Default(string? named) =>
        string.IsNullOrWhiteSpace(named)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".denonremote")
            : Path.GetFullPath(named.Trim());

    public static string Receivers => Path.Combine(Root, "receivers.json");
    public static string Scenes => Path.Combine(Root, "scenes.json");
    public static string Discovery => Path.Combine(Root, "discovery");

    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
