using SQLite;

namespace MusicLib.Core;

public enum TagField { Title, Artist, Album }

/// <summary>
/// One row per media file on THIS device. Disposable: rebuilt by scanning.
/// </summary>
public class Track
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>SHA-256 of the file content (hex). Same on every device.</summary>
    [Indexed]
    public string Hash { get; set; } = "";

    /// <summary>Absolute path on this device.</summary>
    [Unique]
    public string FilePath { get; set; } = "";

    /// <summary>The scanned folder this file was found under.</summary>
    [Indexed]
    public string Root { get; set; } = "";

    /// <summary>Path relative to Root, always with '/' separators.</summary>
    public string RelPath { get; set; } = "";

    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int DurationSeconds { get; set; }
    public bool IsVideo { get; set; }
    public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What THIS device's user has done, keyed by content hash. Only this device writes
/// it; it is exported to this device's own state file, and other devices' files are
/// merged in at read time (see StateSync).
/// Every editable field has its own timestamp so the newest edit wins on merge.
/// A null override WITH a timestamp means "cleared".
/// </summary>
public class UserState
{
    [PrimaryKey]
    public string Hash { get; set; } = "";

    public bool IsFavorite { get; set; }
    public long? FavoriteUpdatedMs { get; set; }

    /// <summary>Plays done on this device only. Totals are summed across devices.</summary>
    public int PlayCount { get; set; }
    public long? LastPlayedMs { get; set; }

    public string? TitleOverride { get; set; }
    public long? TitleUpdatedMs { get; set; }
    public string? ArtistOverride { get; set; }
    public long? ArtistUpdatedMs { get; set; }
    public string? AlbumOverride { get; set; }
    public long? AlbumUpdatedMs { get; set; }
}
