using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using MusicLib.Core;

// Needed to read/write real file paths (where Syncthing puts the files) on Android 11+.
[assembly: UsesPermission(Android.Manifest.Permission.ManageExternalStorage)]

namespace MusicLib.AndroidApp;

[Activity(Label = "MusicLib", MainLauncher = true)]
public class MainActivity : Activity
{
    private const int PickRequest = 1001;
    private const string DefaultHome = "/storage/emulated/0/Music/MusicLib";

    private EditText _pathBox = null!;
    private TextView _deviceLabel = null!;
    private TextView _status = null!;
    private TrackListAdapter _adapter = null!;
    private DeviceSettings _settings = null!;

    private string DataDir => FilesDir!.AbsolutePath;
    private string DbPath => System.IO.Path.Combine(DataDir, "library.db3");

    private static bool HasAccess() => global::Android.OS.Environment.IsExternalStorageManager;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _settings = DeviceSettings.Load(System.IO.Path.Combine(DataDir, "settings.json"));

        // Android 13+: the playback notification needs this runtime permission to be visible.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 2001);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 100, 24, 0);

        _pathBox = new EditText(this) { Text = _settings.HomePath ?? DefaultHome };
        _deviceLabel = new TextView(this) { TextSize = 11 };

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var grant = new Button(this) { Text = "Grant access" };
        var import = new Button(this) { Text = "Import..." };
        var scan = new Button(this) { Text = "Scan" };
        foreach (var b in new[] { grant, import, scan })
            buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _status = new TextView(this) { Text = "Ready" };
        var list = new RecyclerView(this);
        list.SetLayoutManager(new LinearLayoutManager(this));
        _adapter = new TrackListAdapter();
        list.SetAdapter(_adapter);

        root.AddView(_pathBox);
        root.AddView(_deviceLabel);
        root.AddView(buttons);
        root.AddView(_status);
        root.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        grant.Click += (_, _) => RequestAccess();
        import.Click += (_, _) => PickFiles();
        scan.Click += async (_, _) => await ScanAsync(scan);
        _adapter.TrackClicked += OpenPlayer;
        _adapter.FavoriteToggled += ToggleFavorite;
    }

    protected override void OnResume()
    {
        base.OnResume();
        _status.Text = HasAccess() ? "All-files access: granted" : "All-files access: NOT granted — tap Grant access";
        _deviceLabel.Text = $"Device: {_settings.DeviceName}  ({_settings.DeviceId[..8]})";
        LoadTracks(); // also picks up favorite/play-count changes made in the player screen
    }

    private LibraryHome PrepareHome()
    {
        var path = _pathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path)) path = DefaultHome;
        if (_settings.HomePath != path)
        {
            _settings.HomePath = path;
            _settings.Save();
        }
        var home = new LibraryHome(path);
        home.EnsureCreated();
        return home;
    }

    private void RequestAccess()
    {
        if (HasAccess()) { _status.Text = "Access already granted."; return; }
        var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
            global::Android.Net.Uri.Parse($"package:{PackageName}"));
        StartActivity(intent);
    }

    // ---- list ----

    private void LoadTracks()
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            var effective = StateSync.LoadEffective(db, _settings, home);
            var rows = db.GetAllTracks()
                .Select(t => new TrackRow(t, effective.GetValueOrDefault(t.Hash)))
                .ToList();
            _adapter.Submit(rows);
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
        }
    }

    private void OpenPlayer(Track track)
    {
        var home = PrepareHome();
        var intent = new Intent(this, typeof(PlayerActivity));
        intent.PutExtra("filePath", track.FilePath);
        intent.PutExtra("hash", track.Hash);
        intent.PutExtra("title", track.Title);
        intent.PutExtra("artist", track.Artist);
        intent.PutExtra("dataDir", DataDir);
        intent.PutExtra("homePath", home.RootPath);
        StartActivity(intent);
    }

    private void ToggleFavorite(Track track)
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            var current = StateSync.LoadEffective(db, _settings, home).GetValueOrDefault(track.Hash);
            db.SetFavorite(track.Hash, !(current?.IsFavorite ?? false));
            StateSync.Export(db, _settings, home);
            LoadTracks();
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
        }
    }

    // ---- import via the system file picker (works with content:// URIs) ----

    private void PickFiles()
    {
        if (!HasAccess()) { _status.Text = "Grant all-files access first."; return; }
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "audio/*", "video/*" });
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, PickRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickRequest || resultCode != Result.Ok || data is null) return;

        var uris = new List<global::Android.Net.Uri>();
        if (data.ClipData != null)
            for (var i = 0; i < data.ClipData.ItemCount; i++)
            {
                var uri = data.ClipData.GetItemAt(i)?.Uri;
                if (uri != null) uris.Add(uri);
            }
        else if (data.Data != null) uris.Add(data.Data);

        _ = ImportAsync(uris);
    }

    private async Task ImportAsync(List<global::Android.Net.Uri> uris)
    {
        try
        {
            var home = PrepareHome();
            var settings = _settings;
            var dbPath = DbPath;
            var tempDir = System.IO.Path.Combine(DataDir, "tmp");
            var items = uris.Select(u => (Uri: u, Name: GetDisplayName(u))).ToList();

            _status.Text = $"Importing {items.Count} file(s)...";
            var summary = await Task.Run(() =>
            {
                using var db = new LibraryDatabase(dbPath);
                var importer = new LibraryImporter(db, home, tempDir);
                int copied = 0, duplicates = 0, failed = 0;

                foreach (var (uri, name) in items)
                {
                    try
                    {
                        using var stream = ContentResolver!.OpenInputStream(uri);
                        if (stream == null) { failed++; continue; }
                        if (importer.ImportStream(stream, name).WasDuplicate) duplicates++; else copied++;
                    }
                    catch (Exception) { failed++; }
                }

                var scan = new LibraryScanner().Scan(db, new[] { home.MediaDir });
                StateSync.Export(db, settings, home);
                return $"Imported {copied}, already had {duplicates}, failed {failed}. {scan}";
            });
            _status.Text = summary;
            LoadTracks();
        }
        catch (Exception ex)
        {
            _status.Text = $"Import error: {ex.Message}";
        }
    }

    private string GetDisplayName(global::Android.Net.Uri uri)
    {
        using var cursor = ContentResolver!.Query(uri, new[] { OpenableColumns.DisplayName }, null, null, null);
        if (cursor != null && cursor.MoveToFirst())
        {
            var index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
            if (index >= 0) return cursor.GetString(index) ?? "file";
        }
        return uri.LastPathSegment ?? "file";
    }

    // ---- scan ----

    private async Task ScanAsync(Button button)
    {
        if (!HasAccess()) { _status.Text = "Grant all-files access first."; return; }

        button.Enabled = false;
        _status.Text = "Scanning...";
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
            _status.Text = $"Done: {result}";
            LoadTracks();
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
        }
        finally
        {
            button.Enabled = true;
        }
    }
}
