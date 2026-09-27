using MusicLib.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var dataDir = Environment.GetEnvironmentVariable("MUSICLIB_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicLib");

var settings = DeviceSettings.Load(Path.Combine(dataDir, "settings.json"));
if (settings.HomePath is null)
{
    settings.HomePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MusicLib");
    settings.Save();
    Console.WriteLine($"Library home set to {settings.HomePath} (change with: set-home <folder>)");
}

var home = new LibraryHome(settings.HomePath);
home.EnsureCreated();
var rootsStore = new RootsStore(Path.Combine(dataDir, "roots.json"));
using var db = new LibraryDatabase(Path.Combine(dataDir, "library.db3"));

var command = args.FirstOrDefault()?.ToLowerInvariant();
var arg = args.ElementAtOrDefault(1);

switch (command)
{
    case "home":
    {
        Console.WriteLine($"Home:      {home.RootPath}   <- share this folder with Syncthing");
        Console.WriteLine($"Media:     {home.MediaDir}");
        Console.WriteLine($"State:     {home.SyncDir}");
        Console.WriteLine($"Device id: {settings.DeviceId}");
        Console.WriteLine($"Local data (not synced): {dataDir}");
        break;
    }
    case "set-home":
    {
        if (arg is null) return Usage();
        settings.HomePath = Path.GetFullPath(arg);
        settings.Save();
        Console.WriteLine($"Home is now {settings.HomePath} (existing files are not moved). Run 'scan'.");
        break;
    }
    case "add-root":
    {
        if (arg is null) return Usage();
        Console.WriteLine(rootsStore.Add(arg) ? $"Added: {RootsStore.Normalize(arg)}" : "Already in the list.");
        break;
    }
    case "remove-root":
    {
        if (arg is null) return Usage();
        Console.WriteLine(rootsStore.Remove(arg) ? "Removed. Run 'scan' to drop its tracks from the index." : "Not in the list.");
        break;
    }
    case "roots":
    {
        foreach (var r in AllRoots()) Console.WriteLine(r);
        break;
    }
    case "import":
    {
        if (arg is null) return Usage();
        List<string> files;
        if (Directory.Exists(arg))
            files = Directory.EnumerateFiles(arg, "*", SearchOption.AllDirectories).Where(LibraryScanner.IsMediaFile).ToList();
        else if (File.Exists(arg))
            files = new List<string> { arg };
        else { Console.WriteLine("Not found."); return 1; }

        var importer = new LibraryImporter(db, home, Path.Combine(dataDir, "tmp"));
        int copied = 0, duplicates = 0, failed = 0;
        foreach (var f in files)
        {
            try
            {
                if (importer.ImportFile(f).WasDuplicate) duplicates++; else copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed++;
                Console.WriteLine($"  {f}: {ex.Message}");
            }
        }
        Console.WriteLine($"Imported {copied}, already in library {duplicates}, failed {failed}");
        if (copied > 0) Console.WriteLine(new LibraryScanner().Scan(db, AllRoots(), Console.WriteLine));
        break;
    }
    case "scan":
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new LibraryScanner().Scan(db, AllRoots(), Console.WriteLine);
        StateSync.Export(db, settings, home);
        Console.WriteLine($"{result} in {sw.Elapsed.TotalSeconds:F1}s");
        break;
    }
    case "list":
    {
        var favOnly = args.Contains("--fav");
        var effective = StateSync.LoadEffective(db, settings, home);
        foreach (var t in db.GetAllTracks())
        {
            effective.TryGetValue(t.Hash, out var e);
            if (favOnly && e?.IsFavorite != true) continue;
            Console.WriteLine($"{t.Id,5}  {(e?.IsFavorite == true ? "*" : " ")}  {e?.PlayCount ?? 0,3}x  " +
                              $"{e?.Artist ?? t.Artist} - {e?.Title ?? t.Title}  [{t.Hash[..8]}]  {t.RelPath}");
        }
        break;
    }
    case "fav":
    case "unfav":
    case "play": // for now just counts a play; real playback comes later
    {
        if (arg is null || db.Resolve(arg) is not { } track)
        {
            Console.WriteLine("Give a valid track id or hash prefix (see 'list').");
            return 1;
        }
        if (command == "play") db.RegisterPlay(track.Hash);
        else db.SetFavorite(track.Hash, command == "fav");
        StateSync.Export(db, settings, home);
        PrintTrack(track);
        break;
    }
    case "tag":
    {
        if (arg is null || db.Resolve(arg) is not { } tagTrack
            || !Enum.TryParse<TagField>(args.ElementAtOrDefault(2), true, out var field))
        {
            Console.WriteLine("Usage: tag <id|hash> <title|artist|album> [new value]   (no value = clear the override)");
            return 1;
        }
        var value = args.Length > 3 ? string.Join(' ', args.Skip(3)) : null;
        db.SetOverride(tagTrack.Hash, field, value);
        StateSync.Export(db, settings, home);
        PrintTrack(tagTrack);
        break;
    }
    case "sync":
    {
        Console.WriteLine($"Wrote {StateSync.Export(db, settings, home)}");
        var others = StateSync.ReadOtherDevices(settings, home);
        Console.WriteLine(others.Count == 0
            ? "No other device has written a state file yet."
            : $"Merging state from {others.Count} other device(s):");
        foreach (var o in others)
            Console.WriteLine($"  {o.DeviceName} ({o.DeviceId[..8]}): {o.Entries.Count} entries");
        break;
    }
    case "stats":
    {
        Console.WriteLine($"{db.GetAllTracks().Count} files, {db.CountUniqueHashes()} unique by content hash");
        break;
    }
    default:
        return Usage();
}

return 0;

List<string> AllRoots()
{
    var roots = new List<string> { home.MediaDir };
    roots.AddRange(rootsStore.Load());
    return roots;
}

void PrintTrack(Track t)
{
    var e = StateSync.LoadEffective(db, settings, home).GetValueOrDefault(t.Hash);
    Console.WriteLine($"{e?.Artist ?? t.Artist} - {e?.Title ?? t.Title}: favorite={e?.IsFavorite ?? false}, plays={e?.PlayCount ?? 0}");
}

static int Usage()
{
    Console.WriteLine("""
        MusicLib console
          home                      show the library home (the folder to share with Syncthing)
          set-home <folder>         change the library home
          import <file|folder>      copy media into <home>/library (skips duplicates by content)
          add-root <folder>         also scan an outside folder (read-only)
          remove-root <folder>
          roots                     folders that 'scan' covers
          scan                      rebuild the index from the files
          list [--fav]
          fav <id|hash> | unfav <id|hash>
          play <id|hash>             counts a play (no audio yet)
          tag <id|hash> title|artist|album [value]   edit a tag without touching the file
          sync                      write this device's state file, show other devices
          stats
        """);
    return 1;
}
