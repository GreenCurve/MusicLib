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

// SingleTop so tapping the playback notification reuses this window instead of stacking a new one.
[Activity(Label = "MusicLib", MainLauncher = true, LaunchMode = global::Android.Content.PM.LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private const int PickRequest = 1001;
    private const string DefaultHome = "/storage/emulated/0/Music/MusicLib";

    /// <summary>The track currently chosen in the list (what the playback block controls).</summary>
    private sealed record Selection(string FilePath, string Hash, string Title, string Artist);

    // technical block
    private EditText _pathBox = null!;
    private TextView _deviceLabel = null!;
    private TextView _status = null!;
    private DeviceSettings _settings = null!;

    // track navigation block
    private TrackListAdapter _adapter = null!;

    // playback block
    private TextView _artistView = null!;
    private TextView _titleView = null!;
    private SeekBar _volumeBar = null!;
    private SeekBar _seekBar = null!;
    private Button _playPause = null!;
    private TextView _timeView = null!;

    private Selection? _selected;
    private PlaybackService? _service;
    private PlayerConnection? _connection;
    private bool _bound;
    private bool _userSeeking;
    private float? _pendingSeek;
    private Handler _handler = null!;
    private Java.Lang.Runnable _tick = null!;

    private const string PlayGlyph = "\u25B6";        // ▶
    private const string PauseGlyph = "\u275A\u275A";  // ❚❚

    private string DataDir => FilesDir!.AbsolutePath;
    private string DbPath => System.IO.Path.Combine(DataDir, "library.db3");

    private LibVLCSharp.Shared.MediaPlayer? Player => _service?.Player;

    private static bool HasAccess() => global::Android.OS.Environment.IsExternalStorageManager;

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _settings = DeviceSettings.Load(System.IO.Path.Combine(DataDir, "settings.json"));

        // Android 13+: the playback notification needs this runtime permission to be visible.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 2001);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 100, 24, 24);

        // ---- top: technical ----
        _pathBox = new EditText(this) { Text = _settings.HomePath ?? DefaultHome };
        _deviceLabel = new TextView(this) { TextSize = 11 };

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var grant = new Button(this) { Text = "Grant access" };
        var import = new Button(this) { Text = "Import..." };
        var scan = new Button(this) { Text = "Scan" };
        foreach (var b in new[] { grant, import, scan })
            buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _status = new TextView(this) { Text = "Ready" };

        root.AddView(_pathBox);
        root.AddView(_deviceLabel);
        root.AddView(buttons);
        root.AddView(_status);

        // ---- middle: track navigation (takes all remaining height) ----
        var list = new RecyclerView(this);
        list.SetLayoutManager(new LinearLayoutManager(this));
        _adapter = new TrackListAdapter();
        list.SetAdapter(_adapter);
        root.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // ---- bottom: playback ----
        var divider = new View(this);
        divider.SetBackgroundColor(global::Android.Graphics.Color.Argb(60, 128, 128, 128));
        root.AddView(divider, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(1)));
        root.AddView(BuildPlayerBlock());

        SetContentView(root);

        grant.Click += (_, _) => RequestAccess();
        import.Click += (_, _) => PickFiles();
        scan.Click += async (_, _) => await ScanAsync(scan);
        _adapter.TrackClicked += row => SelectTrack(row);

        _playPause.Click += (_, _) => TogglePlayPause();
        _volumeBar.ProgressChanged += (_, e) =>
        {
            if (e.FromUser && Player is { } p) p.Volume = e.Progress;
        };
        _seekBar.StartTrackingTouch += (_, _) => _userSeeking = true;
        _seekBar.StopTrackingTouch += (_, e) =>
        {
            _userSeeking = false;
            OnSeekReleased(e.SeekBar!.Progress / 1000f);
        };

        UpdatePlayerLabels();

        _handler = new Handler(Looper.MainLooper!);
        _tick = new Java.Lang.Runnable(UpdateProgress);
    }

    // Layout follows the playback schema: cover on the left; on the right, name/artist with the
    // volume control above, and play button + seekbar + time below.
    private View BuildPlayerBlock()
    {
        var block = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        block.SetPadding(0, Dp(8), 0, 0);

        // Cover: no cover art is read from the files yet, so this is a placeholder.
        var cover = new TextView(this) { Text = "\u266A", TextSize = 32, Gravity = GravityFlags.Center };
        cover.SetBackgroundColor(global::Android.Graphics.Color.Argb(40, 128, 128, 128));
        block.AddView(cover, new LinearLayout.LayoutParams(Dp(88), Dp(88)));

        var right = new LinearLayout(this) { Orientation = Orientation.Vertical };
        right.SetPadding(Dp(12), 0, 0, 0);

        // top row: artist + track name | volume
        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);

        var names = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _artistView = new TextView(this) { TextSize = 12 };
        _titleView = new TextView(this) { TextSize = 16 };
        foreach (var t in new[] { _artistView, _titleView })
        {
            t.SetSingleLine(true);
            t.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            names.AddView(t);
        }
        top.AddView(names, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var volumeIcon = new TextView(this) { Text = "\U0001F50A", TextSize = 16 };
        _volumeBar = new SeekBar(this) { Max = 100, Progress = 100 };
        top.AddView(volumeIcon);
        top.AddView(_volumeBar, new LinearLayout.LayoutParams(Dp(96), ViewGroup.LayoutParams.WrapContent));

        // bottom row: play | seekbar | time
        var bottom = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bottom.SetGravity(GravityFlags.CenterVertical);

        _playPause = new Button(this) { Text = PlayGlyph };
        _seekBar = new SeekBar(this) { Max = 1000 };
        _timeView = new TextView(this) { Text = FormatTimes(0, 0), TextSize = 12 };
        bottom.AddView(_playPause, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        bottom.AddView(_seekBar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottom.AddView(_timeView);

        right.AddView(top, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        right.AddView(bottom, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        block.AddView(right, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        return block;
    }

    protected override void OnStart()
    {
        base.OnStart();
        _connection = new PlayerConnection(OnServiceChanged);
        _bound = BindService(new Intent(this, typeof(PlaybackService)), _connection, Bind.AutoCreate);
        _handler.RemoveCallbacks(_tick);
        _handler.PostDelayed(_tick, 500);
    }

    protected override void OnStop()
    {
        base.OnStop();
        _handler.RemoveCallbacks(_tick);
        Detach();   // the service (and the music) keeps running without us
    }

    protected override void OnResume()
    {
        base.OnResume();
        _status.Text = HasAccess() ? "All-files access: granted" : "All-files access: NOT granted — tap Grant access";
        _deviceLabel.Text = $"Device: {_settings.DeviceName}  ({_settings.DeviceId[..8]})";
        LoadTracks();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _handler.RemoveCallbacks(_tick);
        Detach();
        // Deliberately NOT stopping the player: it belongs to PlaybackService.
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

    // ---- track navigation ----

    private void LoadTracks()
    {
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            var effective = StateSync.LoadEffective(db, _settings, home);
            var rows = db.GetAllTracks()
                .Select(t => new TrackRow(t, effective.GetValueOrDefault(t.Hash)))
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)   // alphabetical by track name
                .ToList();
            _adapter.Submit(rows);
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
        }
    }

    // Tapping a row selects it and starts playing it.
    private void SelectTrack(TrackRow row)
    {
        _selected = new Selection(row.Track.FilePath, row.Track.Hash, row.Title, row.Artist);
        _pendingSeek = null;
        _seekBar.Progress = 0;
        _timeView.Text = FormatTimes(0, 0);
        _adapter.SelectedHash = _selected.Hash;
        UpdatePlayerLabels();
        StartPlayback(_selected, restart: true);
    }

    // ---- playback ----

    private void StartPlayback(Selection track, bool restart)
    {
        var intent = new Intent(this, typeof(PlaybackService));
        intent.PutExtra("filePath", track.FilePath);
        intent.PutExtra("hash", track.Hash);
        intent.PutExtra("title", track.Title);
        intent.PutExtra("artist", track.Artist);
        intent.PutExtra(PlaybackService.ExtraRestart, restart);
        StartForegroundService(intent);
    }

    private void UpdatePlayerLabels()
    {
        _titleView.Text = _selected?.Title ?? "Nothing selected";
        _artistView.Text = _selected?.Artist ?? "";
    }

    private void TogglePlayPause()
    {
        if (_selected == null) return;
        var player = Player;
        if (player == null || string.IsNullOrEmpty(_service?.FilePath) ||
            player.State is LibVLCSharp.Shared.VLCState.Ended or LibVLCSharp.Shared.VLCState.Stopped or LibVLCSharp.Shared.VLCState.Error)
            StartPlayback(_selected, restart: true);
        else if (player.IsPlaying) player.Pause();
        else player.Play();
    }

    private void OnSeekReleased(float fraction)
    {
        var player = Player;
        if (player == null || _selected == null) return;
        if (player.State is LibVLCSharp.Shared.VLCState.Ended or LibVLCSharp.Shared.VLCState.Stopped)
        {
            // LibVLC tears the stream down at the end; restart it and apply the seek once it's playing.
            _pendingSeek = fraction;
            StartPlayback(_selected, restart: true);
        }
        else if (player.IsSeekable)
        {
            player.Position = fraction;
        }
    }

    private void OnServiceChanged(PlaybackService? service)
    {
        if (service == null) { _service = null; return; }   // service went away

        _service = service;
        service.Playing += OnServicePlaying;
        service.Paused += OnServicePaused;
        service.Ended += OnServiceEnded;

        // The service outlives this window: if it already has a track (rotation, notification tap,
        // coming back from the background), show it as the selection.
        if (!string.IsNullOrEmpty(service.FilePath) && (_selected == null || _selected.FilePath != service.FilePath))
        {
            _selected = new Selection(service.FilePath, service.Hash, service.Title, service.Artist);
            _adapter.SelectedHash = _selected.Hash;
            UpdatePlayerLabels();
        }

        var volume = service.Player?.Volume ?? -1;
        _volumeBar.Progress = volume < 0 ? 100 : Math.Min(volume, 100);

        // Playback may have started before we finished binding, so sync the UI to the current state.
        if (service.Player?.IsPlaying == true) HandlePlaying();
        else _playPause.Text = PlayGlyph;
    }

    private void Detach()
    {
        if (_service != null)
        {
            _service.Playing -= OnServicePlaying;
            _service.Paused -= OnServicePaused;
            _service.Ended -= OnServiceEnded;
            _service = null;
        }
        if (_bound && _connection != null)
        {
            UnbindService(_connection);
            _bound = false;
        }
    }

    private void OnServicePlaying() => RunOnUiThread(HandlePlaying);
    private void OnServicePaused() => RunOnUiThread(() => _playPause.Text = PlayGlyph);
    private void OnServiceEnded() => RunOnUiThread(() => _playPause.Text = PlayGlyph);

    private void HandlePlaying()
    {
        _playPause.Text = PauseGlyph;
        if (_service is { PlayRegistered: false } service)
        {
            service.PlayRegistered = true;
            RegisterPlay(service.Hash);
        }
        if (_pendingSeek is { } seek && Player is { IsSeekable: true } player)
        {
            player.Position = seek;
            _pendingSeek = null;
        }
    }

    private void UpdateProgress()
    {
        var player = Player;
        if (!_userSeeking && player != null && player.Length > 0 && !string.IsNullOrEmpty(_service?.FilePath))
        {
            _seekBar.Progress = (int)(player.Position * 1000);
            _timeView.Text = FormatTimes(player.Time, player.Length);
        }
        _handler.PostDelayed(_tick, 500);
    }

    private static string FormatTimes(long elapsedMs, long totalMs) => $"{FormatMs(elapsedMs)} / {FormatMs(totalMs)}";

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    // Plays are still counted and synced (no UI for it right now) so the data isn't lost
    // while the play-count display is switched off. Registered once, when playback first starts.
    private void RegisterPlay(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        try
        {
            var home = PrepareHome();
            using var db = new LibraryDatabase(DbPath);
            db.RegisterPlay(hash);
            StateSync.Export(db, _settings, home);
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
