namespace MusicLib.Core;

public sealed record ScanResult(int Added, int Updated, int Unchanged, int Removed, int Errors)
{
    public static ScanResult operator +(ScanResult a, ScanResult b) =>
        new(a.Added + b.Added, a.Updated + b.Updated, a.Unchanged + b.Unchanged,
            a.Removed + b.Removed, a.Errors + b.Errors);

    public override string ToString() =>
        $"+{Added} new, ~{Updated} updated, ={Unchanged} unchanged, -{Removed} removed, {Errors} errors";
}

/// <summary>
/// Rebuilds the index from the files. Plain System.IO only, so it needs real
/// filesystem paths (on Android: all-files access, not content:// URIs).
/// </summary>
public sealed class LibraryScanner
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aac", ".wma" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".mov", ".avi" };

    // Syncthing bookkeeping folders — old file versions would show up as duplicates.
    private static readonly string[] IgnoredDirs = { ".stfolder", ".stversions" };

    private const int BatchSize = 100;

    /// <summary>
    /// Pass the COMPLETE set of configured folders: rows under folders that are no longer
    /// in the list (removed roots, a changed home) are dropped from the index.
    /// </summary>
    public ScanResult Scan(LibraryDatabase db, IEnumerable<string> roots, Action<string>? log = null)
    {
        var total = new ScanResult(0, 0, 0, 0, 0);
        var configured = new List<string>();

        foreach (var rawRoot in roots)
        {
            var root = RootsStore.Normalize(rawRoot);
            configured.Add(root);

            if (!Directory.Exists(root))
            {
                // Never treat "folder unreachable" as "all files deleted".
                log?.Invoke($"Folder not found, skipped (nothing removed): {root}");
                total += new ScanResult(0, 0, 0, 0, 1);
                continue;
            }

            log?.Invoke($"Scanning {root}");
            total += ScanRoot(db, root, log);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = new HashSet<string>(configured, comparer);
        var orphanIds = db.GetAllTracks().Where(t => !known.Contains(t.Root)).Select(t => t.Id).ToList();
        if (orphanIds.Count > 0)
        {
            db.ApplyChanges(Array.Empty<Track>(), Array.Empty<Track>(), orphanIds);
            total += new ScanResult(0, 0, 0, orphanIds.Count, 0);
        }

        return total;
    }

    public static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path);
        return AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    private static ScanResult ScanRoot(LibraryDatabase db, string root, Action<string>? log)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var existing = db.GetTracksUnderRoot(root).ToDictionary(t => t.FilePath, comparer);
        var seen = new HashSet<string>(comparer);

        var inserts = new List<Track>();
        var updates = new List<Track>();
        int added = 0, updated = 0, unchanged = 0, removed = 0, errors = 0;

        void Flush()
        {
            if (inserts.Count == 0 && updates.Count == 0) return;
            db.ApplyChanges(inserts, updates, Array.Empty<int>());
            added += inserts.Count;
            updated += updates.Count;
            inserts.Clear();
            updates.Clear();
            log?.Invoke($"  {added + updated} new/changed so far...");
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var enumerationCompleted = false;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                var ext = Path.GetExtension(path);
                var isVideo = VideoExtensions.Contains(ext);
                if (!isVideo && !AudioExtensions.Contains(ext)) continue;
                if (InIgnoredDir(path, root)) continue;

                seen.Add(path);
                var info = new FileInfo(path);
                var mtime = info.LastWriteTimeUtc.Ticks;
                existing.TryGetValue(path, out var old);

                if (old != null && old.SizeBytes == info.Length && old.ModifiedUtcTicks == mtime)
                {
                    unchanged++;
                    continue;
                }

                try
                {
                    var track = BuildTrack(root, path, info, isVideo);
                    if (old == null)
                    {
                        inserts.Add(track);
                    }
                    else
                    {
                        track.Id = old.Id;
                        track.DateAddedUtc = old.DateAddedUtc;
                        updates.Add(track);
                    }
                    if (inserts.Count + updates.Count >= BatchSize) Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    log?.Invoke($"  cannot read {path}: {ex.Message}");
                }
            }
            enumerationCompleted = true;
        }
        catch (Exception ex)
        {
            errors++;
            log?.Invoke($"Scan of {root} aborted: {ex.Message}");
        }

        Flush();

        // Prune only after a complete walk, so a failed scan can't wipe the index.
        if (enumerationCompleted)
        {
            var goneIds = existing.Values.Where(t => !seen.Contains(t.FilePath)).Select(t => t.Id).ToList();
            if (goneIds.Count > 0)
                db.ApplyChanges(Array.Empty<Track>(), Array.Empty<Track>(), goneIds);
            removed = goneIds.Count;
        }

        return new ScanResult(added, updated, unchanged, removed, errors);
    }

    private static bool InIgnoredDir(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Every segment except the last (the file name) is a directory.
        for (int i = 0; i < segments.Length - 1; i++)
            if (IgnoredDirs.Contains(segments[i], StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Track BuildTrack(string root, string path, FileInfo info, bool isVideo)
    {
        var track = new Track
        {
            Hash = FileHasher.ComputeHash(path),
            Root = root,
            FilePath = path,
            RelPath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            SizeBytes = info.Length,
            ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
            IsVideo = isVideo,
            Title = Path.GetFileNameWithoutExtension(path) // fallback if tags are missing
        };

        try
        {
            using var tagFile = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title)) track.Title = tagFile.Tag.Title;
            track.Artist = tagFile.Tag.FirstPerformer ?? "";
            track.Album = tagFile.Tag.Album ?? "";
            track.DurationSeconds = (int)tagFile.Properties.Duration.TotalSeconds;
        }
        catch (Exception)
        {
            // Corrupt or unsupported tags: keep the filename fallback instead of failing the scan.
        }

        return track;
    }
}
