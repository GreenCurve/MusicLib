using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicLib.Core;

/// <summary>One track's user data as written by ONE device.</summary>
public sealed class StateEntry
{
    public int Plays { get; set; }
    public long? LastPlayedMs { get; set; }
    public bool? Fav { get; set; }
    public long? FavAt { get; set; }
    public string? Title { get; set; }
    public long? TitleAt { get; set; }
    public string? Artist { get; set; }
    public long? ArtistAt { get; set; }
    public string? Album { get; set; }
    public long? AlbumAt { get; set; }
}

/// <summary>The file a device writes into .musiclib/. Only its owner ever writes it.</summary>
public sealed class StateFile
{
    public int Version { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public SortedDictionary<string, StateEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The merged view of all devices, used for display.</summary>
public sealed record EffectiveState(
    bool IsFavorite, int PlayCount, long? LastPlayedMs, string? Title, string? Artist, string? Album);

/// <summary>
/// Sync without conflicts: each device writes ONLY its own file, so Syncthing never has two
/// writers for one file. Reading merges everything:
///   play counts      -> sum of each device's own count
///   favorite / tags  -> the entry with the newest timestamp wins, per field
/// </summary>
public static class StateSync
{
    private const string FilePrefix = "state-";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string OwnFilePath(DeviceSettings me, LibraryHome home) =>
        Path.Combine(home.SyncDir, $"{FilePrefix}{me.DeviceId}.json");

    /// <summary>Writes this device's file (only if the content changed). Returns its path.</summary>
    public static string Export(LibraryDatabase db, DeviceSettings me, LibraryHome home)
    {
        home.EnsureCreated();

        var file = new StateFile { DeviceId = me.DeviceId, DeviceName = me.DeviceName };
        foreach (var (hash, entry) in OwnEntries(db)) file.Entries[hash] = entry;

        var json = JsonSerializer.Serialize(file, JsonOpts);
        var path = OwnFilePath(me, home);
        if (File.Exists(path) && File.ReadAllText(path) == json) return path;

        // Write next to the target, then rename, so readers never see a half-written file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static List<StateFile> ReadOtherDevices(DeviceSettings me, LibraryHome home)
    {
        var result = new List<StateFile>();
        if (!Directory.Exists(home.SyncDir)) return result;

        foreach (var path in Directory.EnumerateFiles(home.SyncDir, FilePrefix + "*.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), JsonOpts);
                if (file == null || file.DeviceId == me.DeviceId) continue;
                // Ignore stray copies such as Syncthing conflict files: they would double-count plays.
                if (Path.GetFileName(path) != $"{FilePrefix}{file.DeviceId}.json") continue;
                result.Add(file);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Still being synced or unreadable right now; it will be picked up next time.
            }
        }
        return result;
    }

    public static Dictionary<string, EffectiveState> LoadEffective(LibraryDatabase db, DeviceSettings me, LibraryHome home)
    {
        var sources = new List<IDictionary<string, StateEntry>> { OwnEntries(db) };
        foreach (var other in ReadOtherDevices(me, home)) sources.Add(other.Entries);

        var result = new Dictionary<string, EffectiveState>();
        foreach (var hash in sources.SelectMany(s => s.Keys).Distinct())
        {
            var entries = new List<StateEntry>();
            foreach (var source in sources)
                if (source.TryGetValue(hash, out var entry)) entries.Add(entry);

            result[hash] = new EffectiveState(
                IsFavorite: Newest(entries, e => e.FavAt)?.Fav ?? false,
                PlayCount: entries.Sum(e => e.Plays),
                LastPlayedMs: entries.Max(e => e.LastPlayedMs),
                Title: Newest(entries, e => e.TitleAt)?.Title,
                Artist: Newest(entries, e => e.ArtistAt)?.Artist,
                Album: Newest(entries, e => e.AlbumAt)?.Album);
        }
        return result;
    }

    private static StateEntry? Newest(List<StateEntry> entries, Func<StateEntry, long?> timestamp)
    {
        StateEntry? best = null;
        foreach (var e in entries)
        {
            var t = timestamp(e);
            if (t == null) continue;
            if (best == null || t > timestamp(best)) best = e;
        }
        return best;
    }

    private static SortedDictionary<string, StateEntry> OwnEntries(LibraryDatabase db)
    {
        var result = new SortedDictionary<string, StateEntry>(StringComparer.Ordinal);
        foreach (var s in db.GetAllUserStates())
        {
            var nothing = s.PlayCount == 0 && s.FavoriteUpdatedMs == null && s.TitleUpdatedMs == null
                          && s.ArtistUpdatedMs == null && s.AlbumUpdatedMs == null;
            if (nothing) continue;

            result[s.Hash] = new StateEntry
            {
                Plays = s.PlayCount,
                LastPlayedMs = s.LastPlayedMs,
                Fav = s.FavoriteUpdatedMs != null ? s.IsFavorite : null,
                FavAt = s.FavoriteUpdatedMs,
                Title = s.TitleOverride, TitleAt = s.TitleUpdatedMs,
                Artist = s.ArtistOverride, ArtistAt = s.ArtistUpdatedMs,
                Album = s.AlbumOverride, AlbumAt = s.AlbumUpdatedMs
            };
        }
        return result;
    }
}
