namespace MusicLib.Core;

/// <summary>
/// The app-owned folder. Share THIS folder with Syncthing on every device.
///   library/    media files (imports and downloads land here)
///   .musiclib/  small state files, one per device
/// The SQLite index is NOT in here; it stays in the app's private data folder.
/// </summary>
public sealed class LibraryHome
{
    public LibraryHome(string rootPath)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public string RootPath { get; }
    public string MediaDir => Path.Combine(RootPath, "library");
    public string SyncDir => Path.Combine(RootPath, ".musiclib");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(MediaDir);
        Directory.CreateDirectory(SyncDir);
    }
}
