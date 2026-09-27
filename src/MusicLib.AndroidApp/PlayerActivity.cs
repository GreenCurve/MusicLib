using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using LibVLCSharp.Shared;
using MusicLib.Core;

namespace MusicLib.AndroidApp;

[Activity(Label = "Now playing")]
public class PlayerActivity : Activity
{
    private LibVLC _libVLC = null!;
    private MediaPlayer _mediaPlayer = null!;
    private LibraryDatabase _db = null!;
    private DeviceSettings _settings = null!;
    private LibraryHome _home = null!;

    private string _filePath = "";
    private string _hash = "";
    private bool _playRegistered;
    private bool _userSeeking;

    private TextView _titleView = null!;
    private TextView _elapsed = null!;
    private TextView _duration = null!;
    private SeekBar _seekBar = null!;
    private Button _playPause = null!;
    private TextView _favStar = null!;
    private Handler _handler = null!;
    private Java.Lang.Runnable _tick = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _filePath = Intent?.GetStringExtra("filePath") ?? "";
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
        stop.Click += (_, _) => { _mediaPlayer.Stop(); Finish(); };
        _seekBar.StartTrackingTouch += (_, _) => _userSeeking = true;
        _seekBar.StopTrackingTouch += (_, e) =>
        {
            _userSeeking = false;
            if (_mediaPlayer.IsSeekable) _mediaPlayer.Position = e.SeekBar!.Progress / 1000f;
        };

        // Audio-only: no video output is attached, so this also works fine for the
        // .mp4 files in the library — LibVLC just decodes and plays their audio track.
        _libVLC = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVLC);
        _mediaPlayer.Playing += (_, _) => RunOnUiThread(() =>
        {
            _playPause.Text = "Pause";
            if (!_playRegistered) { _playRegistered = true; RegisterPlay(); }
        });
        _mediaPlayer.Paused += (_, _) => RunOnUiThread(() => _playPause.Text = "Play");
        _mediaPlayer.EndReached += (_, _) => RunOnUiThread(() => _playPause.Text = "Play");

        using var media = new Media(_libVLC, _filePath, FromType.FromPath);
        _mediaPlayer.Play(media);

        _handler = new Handler(Looper.MainLooper!);
        _tick = new Java.Lang.Runnable(UpdateProgress);
        _handler.PostDelayed(_tick, 500);
    }

    private void UpdateProgress()
    {
        if (!_userSeeking && _mediaPlayer.Length > 0)
        {
            _seekBar.Progress = (int)(_mediaPlayer.Position * 1000);
            _elapsed.Text = FormatMs(_mediaPlayer.Time);
            _duration.Text = FormatMs(_mediaPlayer.Length);
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
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
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
        _favStar.Text = current?.IsFavorite == true ? "\u2605" : "\u2606";
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _handler.RemoveCallbacks(_tick);
        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _libVLC.Dispose();
        _db.Dispose();
    }
}
