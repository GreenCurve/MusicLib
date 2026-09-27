namespace MusicLib.Core;

public sealed record ImportResult(string DestinationPath, bool WasDuplicate);

/// <summary>Copies media into the library home, skipping files whose content is already there.</summary>
public sealed class LibraryImporter
{
    private readonly LibraryDatabase _db;
    private readonly LibraryHome _home;
    private readonly string _tempDir;
    private readonly HashSet<string> _hashesThisSession = new();

    /// <param name="tempDir">A LOCAL, non-synced folder used to stage streams before hashing.</param>
    public LibraryImporter(LibraryDatabase db, LibraryHome home, string tempDir)
    {
        _db = db;
        _home = home;
        _tempDir = tempDir;
    }

    public ImportResult ImportFile(string sourcePath)
    {
        RequireMedia(sourcePath);
        var hash = FileHasher.ComputeHash(sourcePath);
        if (IsDuplicate(hash, out var existing)) return new ImportResult(existing, true);

        _home.EnsureCreated();
        var dest = UniqueDestination(Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest);
        _hashesThisSession.Add(hash);
        return new ImportResult(dest, false);
    }

    /// <summary>For sources without a path (Android document picker, uploads).</summary>
    public ImportResult ImportStream(Stream source, string fileName)
    {
        RequireMedia(fileName);
        Directory.CreateDirectory(_tempDir);
        var temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = File.Create(temp)) source.CopyTo(output);

            var hash = FileHasher.ComputeHash(temp);
            if (IsDuplicate(hash, out var existing)) return new ImportResult(existing, true);

            _home.EnsureCreated();
            var dest = UniqueDestination(fileName);
            File.Move(temp, dest);
            _hashesThisSession.Add(hash);
            return new ImportResult(dest, false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// "Duplicate" means already inside the library home — a hash match from an
    /// add-root'd outside folder does NOT count, since import's job is specifically
    /// to get a copy INTO the home so Syncthing carries it to other devices.
    /// </summary>
    private bool IsDuplicate(string hash, out string existingPath)
    {
        existingPath = "";
        if (_hashesThisSession.Contains(hash)) return true;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var track = _db.GetTracksByHash(hash)
		    .FirstOrDefault(t => t.FilePath.StartsWith(_home.MediaDir + Path.DirectorySeparatorChar, comparison));
        if (track == null) return false;

        existingPath = track.FilePath;
        return true;
    }

    private static void RequireMedia(string name)
    {
        if (!LibraryScanner.IsMediaFile(name))
            throw new InvalidOperationException("Not a supported audio/video file.");
    }

    private string UniqueDestination(string fileName)
    {
        var safe = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');

        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var candidate = Path.Combine(_home.MediaDir, safe);
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(_home.MediaDir, $"{stem} ({i}){ext}");
        return candidate;
    }
}
