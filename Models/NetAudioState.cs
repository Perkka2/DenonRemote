namespace DenonRemote.Models;

/// <summary>
/// What the pre-HEOS network player is showing.
///
/// The receiver does not describe what it is playing; it describes its own screen.
/// <see cref="Lines"/> is the ten-line display buffer verbatim, and what each line
/// means depends on what the screen is doing: playing a track it is status, title,
/// artist, album; browsing a folder it is list entries. Treating slot 2 as "the
/// artist" would confidently report a menu item as the artist, so nothing here
/// pretends to know - the panel shows the receiver's screen, which is always true.
/// </summary>
public sealed class NetAudioState
{
    /// <summary>The display buffer, in order, trailing blanks removed.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Per-line flag from chFlag: non-zero marks a line the cursor is on.</summary>
    public List<int> LineFlags { get; } = [];

    /// <summary>The service or source being played - "Spotify", "USB", "Tuner".</summary>
    public string? Service { get; set; }

    /// <summary>The input the receiver reports for this zone.</summary>
    public string? Input { get; set; }

    /// <summary>True when the receiver says it has album art to serve.</summary>
    public bool HasArt { get; set; }

    public bool Repeat { get; set; }
    public bool Shuffle { get; set; }

    /// <summary>True once a document has been read, so the panel can tell apart
    /// "nothing playing" from "not asked yet".</summary>
    public bool Known { get; set; }

    /// <summary>When the last read succeeded, used to bust the album-art cache.</summary>
    public DateTimeOffset ReadAt { get; set; }

    /// <summary>
    /// The first line, which on this firmware is the screen's own heading -
    /// "Now Playing" while playing, the folder name while browsing.
    /// </summary>
    public string? Heading => Lines.Count > 0 && Lines[0].Length > 0 ? Lines[0] : null;

    /// <summary>The lines below the heading, blanks dropped.</summary>
    public IEnumerable<string> Body =>
        Lines.Skip(1).Where(line => line.Length > 0);

    public bool Idle => Lines.All(line => line.Length == 0);
}
