using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
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
    private TextView _log = null!;
    private ScrollView _scroll = null!;
    private DeviceSettings _settings = null!;

    private string DataDir => FilesDir!.AbsolutePath;
    private string DbPath => System.IO.Path.Combine(DataDir, "library.db3");

    private static bool HasAccess() => global::Android.OS.Environment.IsExternalStorageManager;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _settings = DeviceSettings.Load(System.IO.Path.Combine(DataDir, "settings.json"));

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 120, 32, 32);

        _pathBox = new EditText(this) { Text = _settings.HomePath ?? DefaultHome };
        var grant = new Button(this) { Text = "1. Grant all-files access" };
        var import = new Button(this) { Text = "2. Import audio/video files..." };
        var scan = new Button(this) { Text = "3. Scan library" };
        var list = new Button(this) { Text = "4. Show library" };
        _log = new TextView(this);
        _scroll = new ScrollView(this);
        _scroll.AddView(_log);

        layout.AddView(_pathBox);
        layout.AddView(grant);
        layout.AddView(import);
        layout.AddView(scan);
        layout.AddView(list);
        layout.AddView(_scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(layout);

        grant.Click += (_, _) => RequestAccess();
        import.Click += (_, _) => PickFiles();
        scan.Click += async (_, _) => await ScanAsync(scan);
        list.Click += (_, _) => ShowLibrary();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Print(HasAccess() ? "All-files access: granted" : "All-files access: NOT granted");
    }

    /// <summary>The home folder is whatever is in the text box; it is remembered in settings.</summary>
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
        if (HasAccess()) { Print("Access already granted."); return; }
        var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
            global::Android.Net.Uri.Parse($"package:{PackageName}"));
        StartActivity(intent);
    }

    // ---- import via the system file picker (works with content:// URIs) ----

    private void PickFiles()
    {
        if (!HasAccess()) { Print("Grant all-files access first (button 1)."); return; }
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
        {
            for (var i = 0; i < data.ClipData.ItemCount; i++)
            {
                var uri = data.ClipData.GetItemAt(i)?.Uri;
                if (uri != null) uris.Add(uri);
            }
        }
        else if (data.Data != null)
        {
            uris.Add(data.Data);
        }

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

            Print($"Importing {items.Count} file(s)...");
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
                    catch (Exception ex)
                    {
                        failed++;
                        Print($"  {name}: {ex.Message}");
                    }
                }

                var scan = new LibraryScanner().Scan(db, new[] { home.MediaDir }, Print);
                StateSync.Export(db, settings, home);
                return $"Imported {copied}, already had {duplicates}, failed {failed}. Index: {scan}";
            });
            Print(summary);
        }
        catch (Exception ex)
        {
            Print($"Import error: {ex.Message}");
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

    // ---- scan / show ----

    private async Task ScanAsync(Button button)
    {
        if (!HasAccess()) { Print("Grant all-files access first (button 1)."); return; }

        button.Enabled = false;
        try
        {
            var home = PrepareHome();
            var settings = _settings;
            var dbPath = DbPath;
            var result = await Task.Run(() =>
            {
                using var db = new LibraryDatabase(dbPath);
                var r = new LibraryScanner().Scan(db, new[] { home.MediaDir }, Print);
                StateSync.Export(db, settings, home);
                return r;
            });
            Print($"Done: {result}");
        }
        catch (Exception ex)
        {
            Print($"Error: {ex.Message}");
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void ShowLibrary()
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            var effective = StateSync.LoadEffective(db, _settings, home);
            var others = StateSync.ReadOtherDevices(_settings, home).Count;
            var tracks = db.GetAllTracks();

            Print($"{tracks.Count} files, {db.CountUniqueHashes()} unique by hash; state from {others} other device(s)");
            foreach (var t in tracks.Take(40))
            {
                effective.TryGetValue(t.Hash, out var e);
                Print($"{(e?.IsFavorite == true ? "*" : " ")} {t.Id}  {e?.Artist ?? t.Artist} - {e?.Title ?? t.Title}  " +
                      $"[{t.Hash[..8]}]  {t.RelPath}");
            }
        }
        catch (Exception ex)
        {
            Print($"Error: {ex.Message}");
        }
    }

    // Safe to call from any thread.
    private void Print(string message) => RunOnUiThread(() =>
    {
        _log.Append(message + "\n");
        _scroll.FullScroll(FocusSearchDirection.Down);
    });
}
