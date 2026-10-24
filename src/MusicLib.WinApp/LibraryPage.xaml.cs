using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MusicLib.Core;

namespace MusicLib.WinApp;

public sealed partial class LibraryPage : Page
{
    private const string DefaultHome = @"Documents\Music\MusicLib"; // relative to the user profile

    private readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicLib");
    private DeviceSettings _settings = null!;
    private readonly ObservableCollection<TrackItem> _items = new();
    private string? _selectedHash;

    private string DbPath => Path.Combine(_dataDir, "library.db3");

    public LibraryPage()
    {
        InitializeComponent();
        TrackList.ItemsSource = _items;
        Player.PlayStarted += RegisterPlay;

        // Reuses the SAME local data folder as the console app, so this shares its
        // device id, index and settings rather than starting a brand-new "device".
        _settings = DeviceSettings.Load(Path.Combine(_dataDir, "settings.json"));
        PathBox.Text = _settings.HomePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultHome);
        DeviceLabel.Text = $"Device: {_settings.DeviceName}  ({_settings.DeviceId[..8]})";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadTracks();
    }

    private LibraryHome PrepareHome()
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultHome);
        if (_settings.HomePath != path)
        {
            _settings.HomePath = path;
            _settings.Save();
        }
        var home = new LibraryHome(path);
        home.EnsureCreated();
        return home;
    }

    private void LoadTracks()
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            var effective = StateSync.LoadEffective(db, _settings, home);
            var items = db.GetAllTracks()
                .Select(t => new TrackItem { Track = t, State = effective.GetValueOrDefault(t.Hash) })
                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)   // alphabetical by track name
                .ToList();
            _items.Clear();
            foreach (var item in items) _items.Add(item);
            // The list was rebuilt, so re-apply the highlight on the track that is playing.
            TrackList.SelectedItem = _items.FirstOrDefault(i => i.Track.Hash == _selectedHash);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    // Clicking a row selects it and starts playing it in the playback block below.
    private void OnTrackItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TrackItem item) return;
        _selectedHash = item.Track.Hash;
        Player.Play(item);
    }

    // Plays are still counted and synced (no UI for it right now) so the data isn't lost
    // while the play-count display is switched off.
    private void RegisterPlay(Track track)
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            db.RegisterPlay(track.Hash);
            StateSync.Export(db, _settings, home);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    // ---- import ----

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        // Unpackaged apps have no implicit owner window, so the picker needs the HWND explicitly.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindowInstance));
        foreach (var ext in new[] { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aac", ".wma",
                                     ".mp4", ".mkv", ".webm", ".mov", ".avi" })
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        var home = PrepareHome();
        var settings = _settings;
        var dbPath = DbPath;
        var tempDir = Path.Combine(_dataDir, "tmp");
        var paths = files.Select(f => f.Path).ToList();

        StatusText.Text = $"Importing {paths.Count} file(s)...";
        ImportButton.IsEnabled = false;
        try
        {
            var summary = await Task.Run(() =>
            {
                using var db = new LibraryDatabase(dbPath);
                var importer = new LibraryImporter(db, home, tempDir);
                int copied = 0, duplicates = 0, failed = 0;
                foreach (var path in paths)
                {
                    try { if (importer.ImportFile(path).WasDuplicate) duplicates++; else copied++; }
                    catch (Exception) { failed++; }
                }
                var scan = new LibraryScanner().Scan(db, new[] { home.MediaDir });
                StateSync.Export(db, settings, home);
                return $"Imported {copied}, already had {duplicates}, failed {failed}. {scan}";
            });
            StatusText.Text = summary;
            LoadTracks();
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    // ---- scan ----

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning...";
        try
        {
            var home = PrepareHome();
            var settings = _settings;
            var dbPath = DbPath;
            var result = await Task.Run(() =>
            {
                using var db = new LibraryDatabase(dbPath);
                var r = new LibraryScanner().Scan(db, new[] { home.MediaDir });
                StateSync.Export(db, settings, home);
                return r;
            });
            StatusText.Text = $"Done: {result}";
            LoadTracks();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }
}
