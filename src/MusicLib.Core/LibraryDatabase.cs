using SQLite;

namespace MusicLib.Core;

public sealed class LibraryDatabase : IDisposable
{
    private readonly SQLiteConnection _db;

    public LibraryDatabase(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SQLiteConnection(dbPath);
        _db.CreateTable<Track>();
        _db.CreateTable<UserState>();
    }

    // ---- tracks ----

    public List<Track> GetAllTracks() =>
        _db.Table<Track>().OrderBy(t => t.Artist).ThenBy(t => t.Album).ThenBy(t => t.Title).ToList();

    public List<Track> GetTracksUnderRoot(string root) =>
        _db.Table<Track>().Where(t => t.Root == root).ToList();

    public Track? GetTrack(int id) => _db.Find<Track>(id);

    public Track? FindByHash(string hash) =>
        _db.Table<Track>().Where(t => t.Hash == hash).FirstOrDefault();

    public List<Track> GetTracksByHash(string hash) =>
        _db.Table<Track>().Where(t => t.Hash == hash).ToList();

    public int CountUniqueHashes() =>
        _db.ExecuteScalar<int>("SELECT COUNT(DISTINCT Hash) FROM Track");

    /// <summary>Applies a batch of changes in one transaction.</summary>
    public void ApplyChanges(IEnumerable<Track> inserts, IEnumerable<Track> updates, IEnumerable<int> deleteIds)
    {
        _db.RunInTransaction(() =>
        {
            foreach (var t in inserts) _db.Insert(t);
            foreach (var t in updates) _db.Update(t);
            foreach (var id in deleteIds) _db.Delete<Track>(id);
        });
    }

    // ---- this device's own user state ----

    public List<UserState> GetAllUserStates() => _db.Table<UserState>().ToList();

    public UserState GetOwnState(string hash) =>
        _db.Find<UserState>(hash) ?? new UserState { Hash = hash };

    public void SetFavorite(string hash, bool value)
    {
        var s = GetOwnState(hash);
        s.IsFavorite = value;
        s.FavoriteUpdatedMs = Clock.NowMs;
        _db.InsertOrReplace(s);
    }

    public void RegisterPlay(string hash)
    {
        var s = GetOwnState(hash);
        s.PlayCount++;
        s.LastPlayedMs = Clock.NowMs;
        _db.InsertOrReplace(s);
    }

    /// <summary>Sets a tag override; a null or empty value clears it (falls back to the file's tag).</summary>
    public void SetOverride(string hash, TagField field, string? value)
    {
        var s = GetOwnState(hash);
        var now = Clock.NowMs;
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        switch (field)
        {
            case TagField.Title: s.TitleOverride = value; s.TitleUpdatedMs = now; break;
            case TagField.Artist: s.ArtistOverride = value; s.ArtistUpdatedMs = now; break;
            case TagField.Album: s.AlbumOverride = value; s.AlbumUpdatedMs = now; break;
        }
        _db.InsertOrReplace(s);
    }

    public void Dispose() => _db.Dispose();
}
