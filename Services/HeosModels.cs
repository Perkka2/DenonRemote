using System.Text.Json;
using System.Web;

namespace DenonRemote.Services;

/// <summary>One player on the HEOS network, as get_players describes it.</summary>
public sealed class HeosPlayer
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Model { get; init; }

    /// <summary>The pid of the group leader; absent when the player is on its own.</summary>
    public int? Gid { get; init; }
    public string? Ip { get; init; }

    /// <summary>The player that is the device this client connected to, as opposed to another room.</summary>
    public bool IsHost { get; init; }

    public int? Volume { get; set; }
    public bool Muted { get; set; }
}

public sealed record HeosQueueItem(int Qid, string? Song, string? Artist, string? Album, string? ImageUrl);

/// <summary>A top-level source: a streaming service, the local library, playlists, inputs.</summary>
public sealed record HeosSource(int Sid, string Name, string Type, string? ImageUrl, bool Available);

/// <summary>One row of a browse or search result.</summary>
public sealed record HeosItem(
    string Name,
    string Type,
    string? ImageUrl,
    string? Cid,
    string? Mid,
    int? Sid,
    bool Container,
    bool Playable,
    string? Artist,
    string? Album)
{
    /// <summary>A server or service that has to be browsed into, rather than media.</summary>
    public bool IsSource =>
        Sid is not null && Cid is null && (Type is "heos_server" or "heos_service" or "dlna_server");
}

/// <summary>One page of results. Count is the total, and 0 means the source does not know.</summary>
public sealed record HeosPage(IReadOnlyList<HeosItem> Items, int Returned, int Count);

public sealed record HeosSearchCriterion(string Name, string Scid, bool Wildcard);

/// <summary>The answer to one sequenced request.</summary>
public sealed record HeosReply(bool Success, string Message, JsonElement Payload)
{
    public string ErrorText =>
        HttpUtility.ParseQueryString(Message)["text"] is { Length: > 0 } text
            ? text
            : "HEOS refused the request.";

    public static HeosReply Failed(string text) => new(false, "text=" + text, default);
}

public sealed class HeosException(string message) : Exception(message);
