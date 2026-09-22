namespace DenonRemote.Models;

/// <summary>One settings screen in the pre-HEOS setup tree.</summary>
/// <param name="Section">Which menu it hangs off, for grouping.</param>
/// <param name="Label">A short name, used until the page says its own.</param>
/// <param name="Path">The content frame - d_*.asp - not the frameset around it.</param>
public sealed record AspGroup(string Section, string Label, string Path);

/// <summary>
/// The settings screens an AVR-X4100W serves, taken from crawling its own menus
/// rather than from a manual (see the README). Each entry points at the content
/// frame: the f_*.asp beside it is only a frameset, and the s_*.asp is where its
/// form posts, which nothing here asks for.
///
/// A receiver that does not have one of these simply 404s it, and it is left out.
/// </summary>
public static class AspCatalog
{
    public static readonly IReadOnlyList<AspGroup> Groups =
    [
        new("Speakers", "Speaker config",  "/SETUP/SPEAKERS/SPEAKERCONFIG/d_speakersetup.asp"),
        new("Speakers", "Amp assign",      "/SETUP/SPEAKERS/AMPASSIGN/d_speakersetup.asp"),
        new("Speakers", "Crossovers",      "/SETUP/SPEAKERS/CROSSOVERS/d_speakersetup.asp"),
        new("Speakers", "Distances",       "/SETUP/SPEAKERS/DISTANCES/d_speakersetup.asp"),
        new("Speakers", "Levels",          "/SETUP/SPEAKERS/LEVELS/d_speakersetup.asp"),
        new("Speakers", "Bass",            "/SETUP/SPEAKERS/BASS/d_speakersetup.asp"),
        new("Speakers", "2ch playback",    "/SETUP/SPEAKERS/2CHPLAYBACK/d_speakersetup.asp"),

        new("Audio",    "Volume",          "/SETUP/AUDIO/VOLUME/d_audio.asp"),
        new("Audio",    "Graphic EQ",      "/SETUP/AUDIO/GRAPHICEQ/d_audio.asp"),
        new("Audio",    "Surround",        "/SETUP/AUDIO/SURROUNDPARAMETER/d_audio.asp"),
        new("Audio",    "Restorer",        "/SETUP/AUDIO/RESTORER/d_audio.asp"),
        new("Audio",    "Audio delay",     "/SETUP/AUDIO/AUDIODELAY/d_audio.asp"),
        new("Audio",    "Subwoofer level", "/SETUP/AUDIO/SUBWOOFERLEVEL/d_audio.asp"),

        new("Inputs",   "Input assign",    "/SETUP/INPUTS/INPUTASSIGN/d_InputAssign.asp"),
        new("Inputs",   "Source rename",   "/SETUP/INPUTS/SOURCERENAME/d_Rename.asp"),
        new("Inputs",   "Hide sources",    "/SETUP/INPUTS/HIDESOURCES/d_Delete.asp"),
        new("Inputs",   "Source level",    "/SETUP/INPUTS/SOURCELEVEL/d_inputsetup.asp"),

        new("Video",    "HDMI setup",      "/SETUP/VIDEO/HDMISETUP/d_video.asp"),
        new("Video",    "Output settings", "/SETUP/VIDEO/OUTPUTSETTINGS/d_video.asp"),
        new("Video",    "Picture adjust",  "/SETUP/VIDEO/PICTUREADJUST/d_video.asp"),
        new("Video",    "Component out",   "/SETUP/VIDEO/COMPONENTVIDEO/d_video.asp"),
        new("Video",    "On-screen display", "/SETUP/VIDEO/ONSCREENDISPLAY/d_video.asp"),
        new("Video",    "TV format",       "/SETUP/VIDEO/TVFORMAT/d_video.asp"),

        new("General",  "ECO",             "/SETUP/GENERAL/ECO/d_general.asp"),
        new("General",  "Zone 2 setup",    "/SETUP/GENERAL/ZONE2SETUP/d_general.asp"),
        new("General",  "Zone 3 setup",    "/SETUP/GENERAL/ZONE3SETUP/d_general.asp"),
        new("General",  "Zone rename",     "/SETUP/GENERAL/ZONERENAME/d_general.asp"),
        new("General",  "Select names",    "/SETUP/GENERAL/SELECTNAMES/d_general.asp"),
        new("General",  "Front display",   "/SETUP/GENERAL/FRONTDISPLAY/d_general.asp"),
        new("General",  "Trigger out",     "/SETUP/GENERAL/TRIGGEROUT/d_general.asp"),
        new("General",  "Trigger out 2",   "/SETUP/GENERAL/TRIGGEROUT2/d_general.asp"),
        new("General",  "Setup lock",      "/SETUP/GENERAL/SETUPLOCK/d_general.asp"),
        new("General",  "Language",        "/SETUP/GENERAL/LANGUAGE/d_general.asp"),
        new("General",  "Usage data",      "/SETUP/GENERAL/USAGEDATA/d_general.asp"),

        new("Network",  "Friendly name",   "/SETUP/NETWORK/FRIENDLYNAME/d_network.asp"),
        new("Network",  "IP control",      "/SETUP/NETWORK/IPCONTROL/d_network.asp"),

        // The Option menu, which the setup tree does not list but the zone pages link.
        new("Option",   "Tone",            "/OPTION/TONE/d_option.asp"),
        new("Option",   "Channel levels",  "/OPTION/CHANNELLEVELADJUST/d_option.asp"),
        new("Option",   "All-zone stereo", "/OPTION/ALLZONESTEREO/d_option.asp"),
    ];

    public static IEnumerable<string> Sections =>
        Groups.Select(g => g.Section).Distinct();
}
