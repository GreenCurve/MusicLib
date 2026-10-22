using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using LibVLCSharp.Shared;
using MusicLib.Core;

namespace MusicLib.AndroidApp;

// SingleTop so tapping the playback notification reuses this screen instead of stacking a new one.
[Activity(Label = "Now playing", LaunchMode = LaunchMode.SingleTop)]
public class PlayerActivity : Activity
{
    private PlaybackService? _service;
    private PlayerConnection? _connection;
    private bool _bound;
    private LibraryDatabase _db = null!;
    private DeviceSettings _settings = null!;
    private LibraryHome _home = null!;

    private string _hash = "";
    private bool _userSeeking;
    private float? _pendingSeek;

    private TextView _titleView = null!;
    private TextView _elapsed = null!;
    private TextView _duration = null!;
    private SeekBar _seekBar = null!;
    private Button _playPause = null!;
    private TextView _favStar = null!;
    private Handler _handler = null!;
    private Java.Lang.Runnable _tick = null!;

    private MediaPlayer? Player => _service?.Player;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _hash = Intent?.GetStringExtra("hash") ?? "";
        var title = Intent?.GetStringExtra("title") ?? "";
        var artist = Intent?.GetStringExtra("artist") ?? "";
        var dataDir = Intent?.GetStringExtra("dataDir") ?? FilesDir!.AbsolutePath;
        var homePath = Intent?.GetStringExtra("homePath") ?? "";

        _settings = DeviceSettings.Load(System.IO.Path.Combine(dataDir, "settings.json"));
        _home = new LibraryHome(homePath);
        _db = new LibraryDatabase(System.IO.Path.Combine(dataDir, "library.db3"));

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(32, 100, 32, 32);

        _titleView = new TextView(this) { TextSize = 20, Text = string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}" };
        _seekBar = new SeekBar(this) { Max = 1000 };

        var times = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _elapsed = new TextView(this) { Text = "0:00" };
        _duration = new TextView(this) { Text = "0:00", Gravity = GravityFlags.Right };
        times.AddView(_elapsed, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        times.AddView(_duration, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _playPause = new Button(this) { Text = "Pause" };
        var stop = new Button(this) { Text = "Stop" };
        _favStar = new TextView(this) { TextSize = 24, Gravity = GravityFlags.Center };
        _favStar.SetPadding(24, 0, 24, 0);
        foreach (var v in new View[] { _playPause, stop })
            controls.AddView(v, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        controls.AddView(_favStar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        root.AddView(_titleView);
        root.AddView(_seekBar);
        root.AddView(times);
        root.AddView(controls);
        SetContentView(root);

        UpdateFavStar();
        _favStar.Click += (_, _) => ToggleFavorite();
        _playPause.Click += (_, _) => TogglePlayPause();
        stop.Click += (_, _) => { _service?.StopPlayback(); Finish(); };
        _seekBar.StartTrackingTouch += (_, _) => _userSeeking = true;
        _seekBar.StopTrackingTouch += (_, e) =>
        {
            _userSeeking = false;
            var player = Player;
            if (player == null) return;
            var fraction = e.SeekBar!.Progress / 1000f;
            if (player.State is VLCState.Ended or VLCState.Stopped)
            {
                _pendingSeek = fraction;
                _service?.PlayFile();
            }
            else if (player.IsSeekable)
            {
                player.Position = fraction;
            }
        };

        // Hand playback to the foreground service. A fresh tap from the track list restarts the
        // track; rotation or a notification tap (an existing session) must not.
        var fromNotification = Intent?.GetBooleanExtra(PlaybackService.ExtraFromNotification, false) ?? false;
        var svcIntent = new Intent(this, typeof(PlaybackService));
        if (Intent?.Extras != null) svcIntent.PutExtras(Intent.Extras);
        svcIntent.PutExtra(PlaybackService.ExtraRestart, savedInstanceState == null && !fromNotification);
        StartForegroundService(svcIntent);

        _handler = new Handler(Looper.MainLooper!);
        _tick = new Java.Lang.Runnable(UpdateProgress);
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

    private void OnServiceChanged(PlaybackService? service)
    {
        if (service == null) { _service = null; return; }   // service went away

        _service = service;
        service.Playing += OnServicePlaying;
        service.Paused += OnServicePaused;
        service.Ended += OnServiceEnded;

        // Playback may have started before we finished binding, so sync the UI to the current state.
        if (service.Player?.IsPlaying == true) HandlePlaying();
        else _playPause.Text = "Play";
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
    private void OnServicePaused() => RunOnUiThread(() => _playPause.Text = "Play");
    private void OnServiceEnded() => RunOnUiThread(() => _playPause.Text = "Play");

    private void HandlePlaying()
    {
        _playPause.Text = "Pause";
        if (_service is { PlayRegistered: false } service)
        {
            service.PlayRegistered = true;
            RegisterPlay();
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
        if (!_userSeeking && player != null && player.Length > 0)
        {
            _seekBar.Progress = (int)(player.Position * 1000);
            _elapsed.Text = FormatMs(player.Time);
            _duration.Text = FormatMs(player.Length);
        }
        _handler.PostDelayed(_tick, 500);
    }

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void TogglePlayPause()
    {
        var player = Player;
        if (player == null) return;
        if (player.IsPlaying) player.Pause();
        else if (player.State is VLCState.Ended or VLCState.Stopped) _service?.PlayFile();
        else player.Play();
    }

    // Registered once, when playback first starts — no "played 50%" threshold yet.
    private void RegisterPlay()
    {
        _db.RegisterPlay(_hash);
        StateSync.Export(_db, _settings, _home);
    }

    private void ToggleFavorite()
    {
        var current = StateSync.LoadEffective(_db, _settings, _home).GetValueOrDefault(_hash);
        _db.SetFavorite(_hash, !(current?.IsFavorite ?? false));
        StateSync.Export(_db, _settings, _home);
        UpdateFavStar();
    }

    private void UpdateFavStar()
    {
        var current = StateSync.LoadEffective(_db, _settings, _home).GetValueOrDefault(_hash);
        _favStar.Text = current?.IsFavorite == true ? "★" : "☆";
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _handler.RemoveCallbacks(_tick);
        Detach();
        // Deliberately NOT stopping the player: it belongs to PlaybackService now.
        _db.Dispose();
    }
}
