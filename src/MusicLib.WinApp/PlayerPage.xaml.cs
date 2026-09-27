using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using LibVLCSharp.Shared;
using MusicLib.Core;

namespace MusicLib.WinApp;

public sealed partial class PlayerPage : Page
{
    private LibVLC _libVLC = null!;
    private MediaPlayer _mediaPlayer = null!;
    private LibraryDatabase _db = null!;
    private DeviceSettings _settings = null!;
    private LibraryHome _home = null!;
    private Track _track = null!;

    private bool _playRegistered;
    private bool _userSeeking;
    private DispatcherQueueTimer _timer = null!;
    public PlayerPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var context = App.PendingPlayerContext
            ?? throw new InvalidOperationException("PlayerPage opened without a track context.");
        App.PendingPlayerContext = null;
        var (track, dataDir, homePath) = context;
        _track = track;
        _settings = DeviceSettings.Load(Path.Combine(dataDir, "settings.json"));
        _home = new LibraryHome(homePath);
        _db = new LibraryDatabase(Path.Combine(dataDir, "library.db3"));

        var artist = string.IsNullOrEmpty(_track.Artist) ? "" : _track.Artist + " - ";
        TitleText.Text = artist + _track.Title;
        UpdateFavButton();

        // Audio-only: no video output attached, so this also plays the .mp4 files fine —
        // LibVLC just decodes their audio track. Same approach as the Android player.
        _libVLC = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVLC);
        _mediaPlayer.Playing += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            PlayPauseButton.Content = "Pause";
            if (!_playRegistered) { _playRegistered = true; RegisterPlay(); }
        });
        _mediaPlayer.Paused += (_, _) => DispatcherQueue.TryEnqueue(() => PlayPauseButton.Content = "Play");
        _mediaPlayer.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() => PlayPauseButton.Content = "Play");

        using var media = new Media(_libVLC, _track.FilePath, FromType.FromPath);
        _mediaPlayer.Play(media);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => UpdateProgress();
        _timer.Start();
    }

    private void UpdateProgress()
    {
        if (!_userSeeking && _mediaPlayer.Length > 0)
        {
            SeekSlider.Value = _mediaPlayer.Position * 1000;
            ElapsedText.Text = FormatMs(_mediaPlayer.Time);
            DurationText.Text = FormatMs(_mediaPlayer.Length);
        }
    }

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void OnSeekPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _userSeeking = true;

    private void OnSeekReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_mediaPlayer.IsSeekable) _mediaPlayer.Position = (float)(SeekSlider.Value / 1000.0);
        _userSeeking = false;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
        Frame.GoBack();
    }

    // Registered once, when playback first starts — no "played 50%" threshold yet,
    // same simplification as the Android player.
    private void RegisterPlay()
    {
        _db.RegisterPlay(_track.Hash);
        StateSync.Export(_db, _settings, _home);
    }

    private void OnFavClick(object sender, RoutedEventArgs e)
    {
        var current = StateSync.LoadEffective(_db, _settings, _home).GetValueOrDefault(_track.Hash);
        _db.SetFavorite(_track.Hash, !(current?.IsFavorite ?? false));
        StateSync.Export(_db, _settings, _home);
        UpdateFavButton();
    }

    private void UpdateFavButton()
    {
        var current = StateSync.LoadEffective(_db, _settings, _home).GetValueOrDefault(_track.Hash);
        FavButton.Content = current?.IsFavorite == true ? "\u2605 Favorited" : "\u2606 Favorite";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer.Stop();
        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _libVLC.Dispose();
        _db.Dispose();
    }
}
