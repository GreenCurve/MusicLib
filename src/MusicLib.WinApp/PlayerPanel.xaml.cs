using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LibVLCSharp.Shared;
using MusicLib.Core;

namespace MusicLib.WinApp;

/// <summary>
/// The playback block: shows and controls the currently selected track. Lives at the bottom of
/// the library window, so playback no longer depends on a separate page.
/// </summary>
public sealed partial class PlayerPanel : UserControl
{
    private const string PlayGlyph = "\u25B6";        // ▶
    private const string PauseGlyph = "\u275A\u275A";  // ❚❚

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Track? _track;

    private bool _playRegistered;
    private bool _userSeeking;
    private float? _pendingSeek;
    private readonly DispatcherQueueTimer _timer;

    /// <summary>Raised on the UI thread once per track start, so the owner can count the play.</summary>
    public event Action<Track>? PlayStarted;

    public PlayerPanel()
    {
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => UpdateProgress();
        _timer.Start();

        Unloaded += (_, _) => Shutdown();
    }

    /// <summary>Selects the track and starts playing it from the beginning.</summary>
    public void Play(TrackItem item)
    {
        EnsurePlayer();
        _track = item.Track;
        _playRegistered = false;
        _pendingSeek = null;

        ArtistText.Text = item.Artist;
        TitleText.Text = item.Title;
        SeekSlider.Value = 0;
        TimeText.Text = FormatTimes(0, 0);

        PlayFromCurrentFile();
    }

    private void EnsurePlayer()
    {
        if (_mediaPlayer != null) return;

        // Audio-only: no video output attached, so this also plays the .mp4 files fine —
        // LibVLC just decodes their audio track. Same approach as the Android player.
        _libVLC = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVLC) { Volume = (int)VolumeSlider.Value };
        _mediaPlayer.Playing += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            PlayPauseButton.Content = PauseGlyph;
            if (!_playRegistered && _track != null) { _playRegistered = true; PlayStarted?.Invoke(_track); }
            if (_pendingSeek is { } seek && _mediaPlayer.IsSeekable)
            {
                _mediaPlayer.Position = seek;
                _pendingSeek = null;
            }
        });
        _mediaPlayer.Paused += (_, _) => DispatcherQueue.TryEnqueue(() => PlayPauseButton.Content = PlayGlyph);
        _mediaPlayer.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() => PlayPauseButton.Content = PlayGlyph);
    }

    // Once playback reaches the end, LibVLC tears the stream down — just setting Position
    // afterward does nothing. Restarting the media and applying the seek once it's playing
    // again is what actually resumes it.
    private void PlayFromCurrentFile()
    {
        if (_libVLC == null || _mediaPlayer == null || _track == null) return;
        using var media = new Media(_libVLC, _track.FilePath, FromType.FromPath);
        _mediaPlayer.Play(media);
    }

    private void UpdateProgress()
    {
        if (_mediaPlayer == null || _userSeeking || _mediaPlayer.Length <= 0) return;
        SeekSlider.Value = _mediaPlayer.Position * 1000;
        TimeText.Text = FormatTimes(_mediaPlayer.Time, _mediaPlayer.Length);
    }

    private static string FormatTimes(long elapsedMs, long totalMs) => $"{FormatMs(elapsedMs)} / {FormatMs(totalMs)}";

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void OnSeekPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _userSeeking = true;

    private void OnSeekReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_mediaPlayer != null && _track != null)
        {
            var fraction = (float)(SeekSlider.Value / 1000.0);
            if (_mediaPlayer.State is VLCState.Ended or VLCState.Stopped)
            {
                _pendingSeek = fraction;
                PlayFromCurrentFile();
            }
            else if (_mediaPlayer.IsSeekable)
            {
                _mediaPlayer.Position = fraction;
            }
        }
        _userSeeking = false;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null || _track == null) return;
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else if (_mediaPlayer.State is VLCState.Ended or VLCState.Stopped or VLCState.Error) PlayFromCurrentFile();
        else _mediaPlayer.Play();
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer != null) _mediaPlayer.Volume = (int)e.NewValue;
    }

    private void Shutdown()
    {
        _timer.Stop();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        _mediaPlayer = null;
        _libVLC = null;
    }
}
