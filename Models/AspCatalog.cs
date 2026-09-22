namespace DenonRemote.Models;

/// <summary>One settings screen in the pre-HEOS setup tree.</summary>
/// <param name="Section">Which menu it hangs off, in the receiver's own words.</param>
/// <param name="Label">What the receiver calls it.</param>
/// <param name="Path">Its frameset; the content frame inside is found when opened.</param>
public sealed record AspGroup(string Section, string Label, string Path);

/// <summary>Where to start reading a receiver's menus.</summary>
/// <param name="Path">A frameset whose menu frame lists what is under it.</param>
/// <param name="Section">
/// Set when this root is itself one menu rather than a list of them. Null means the
/// page lists sections, each with a menu of its own.
/// </param>
public sealed record AspRoot(string Path, string? Section = null);

/// <summary>
/// The two entry points into the pre-HEOS menus. Everything below them is read from
/// the receiver, not listed here.
///
/// An earlier version of this file was a hand-written list of all 39 screens, built
/// by assuming a content frame is always named d_*.asp. Network's Connection and
/// Settings screens use r_network_setting_dhcp.asp, so they were quietly missing,
/// and nothing could have noticed but a person looking for them. The receiver
/// publishes its own menus; that is the list.
/// </summary>
public static class AspCatalog
{
    public static readonly IReadOnlyList<AspRoot> Roots =
    [
        // Lists the sections - Audio, Video, Inputs, Speakers, Network, General -
        // each of which has a menu of its own.
        new("/SETUP/f_home.asp"),

        // The Option menu is not under Setup. The zone pages link it directly, and
        // it is one menu rather than a list of them.
        new("/OPTION/f_home.asp", "Option"),
    ];
}
