using System.Text.Json.Serialization;

namespace Jellytrim.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
public enum MediaKind { Movie = 1, Series = 2, Season = 3, Episode = 4 }

/// <summary>
/// Per-user state as Jellyfin reports it. <see cref="Played"/> is a flag, not proof of completed
/// playback: a user can mark an item watched manually and Jellyfin does not record which happened.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WatchState>))]
public enum WatchState { Unplayed = 0, Played = 1, Partial = 2, Unknown = 3 }

/// <summary>Whether jellytrim could confirm the files behind an item, as of the last observation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ItemHealth>))]
public enum ItemHealth
{
    /// <summary>Every referenced file was observed.</summary>
    Ok = 0,

    /// <summary>The root is online but a referenced file is gone: a stale Jellyfin entry.</summary>
    Missing = 1,

    /// <summary>Root offline or I/O failure. Says nothing about whether the files still exist.</summary>
    Unavailable = 2,

    /// <summary>Virtual, remote-stream or otherwise non-file item. Never a deletion target.</summary>
    NotFileBacked = 3,

    /// <summary>A container (series or season), whose health is derived from its episodes.</summary>
    Container = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<FileRole>))]
public enum FileRole
{
    /// <summary>The primary video file.</summary>
    Main = 0,

    /// <summary>Additional part of a stacked item (cd1/cd2).</summary>
    Part = 1,

    /// <summary>A file of an alternate version of the same item.</summary>
    AlternateVersion = 2,

    /// <summary>Subtitle, NFO or artwork discovered next to the media at planning time.</summary>
    Sidecar = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProbeStatus>))]
public enum ProbeStatus { Ok = 0, NotFound = 1, AccessDenied = 2, IoError = 3, Unsupported = 4 }

[JsonConverter(typeof(JsonStringEnumConverter<ProtectionTarget>))]
public enum ProtectionTarget { Item = 0, Library = 1 }
