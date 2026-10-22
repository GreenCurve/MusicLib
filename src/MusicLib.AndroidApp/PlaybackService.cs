using Android.App;
using Android.Content;
using Android.OS;
using LibVLCSharp.Shared;

// Foreground-service + wake-lock permissions. POST_NOTIFICATIONS is requested at runtime
// in MainActivity (Android 13+); the service still runs if the user declines, but the
// notification is hidden.
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK")]
[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]
[assembly: UsesPermission("android.permission.WAKE_LOCK")]

namespace MusicLib.AndroidApp;

public class PlaybackBinder : Binder
{
    public PlaybackBinder(PlaybackService service) { Service = service; }
    public PlaybackService Service { get; }
}

public class PlayerConnection : Java.Lang.Object, IServiceConnection
{
    private readonly Action<PlaybackService?> _onChanged;
    public PlayerConnection(Action<PlaybackService?> onChanged) { _onChanged = onChanged; }

    public void OnServiceConnected(ComponentName? name, IBinder? service)
        => _onChanged((service as PlaybackBinder)?.Service);

    public void OnServiceDisconnected(ComponentName? name) => _onChanged(null);
}

// Owns LibVLC + the MediaPlayer so playback no longer depends on the Activity being alive.
// Running as a foreground "media playback" service (with a notification) is what stops
// Android from freezing/killing the app a few minutes after it is minimized.
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
public class PlaybackService : Service
{
    public const string ExtraRestart = "restart";
    public const string ExtraFromNotification = "fromNotification";
    public const string ActionStop = "com.companyname.musiclib.STOP";

    private const string ChannelId = "playback";
    private const int NotificationId = 1;

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private PowerManager.WakeLock? _wakeLock;
    private IBinder? _binder;
    private Bundle? _openExtras;   // extras PlayerActivity needs, replayed by the notification tap
    private string _title = "";
    private string _artist = "";

    public MediaPlayer? Player => _player;
    public string FilePath { get; private set; } = "";
    public bool PlayRegistered { get; set; }

    // Raised on LibVLC's threads — subscribers must marshal to the UI thread themselves.
    public event Action? Playing;
    public event Action? Paused;
    public event Action? Ended;

    public override void OnCreate()
    {
        base.OnCreate();
        _binder = new PlaybackBinder(this);

        var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);

        _libVLC = new LibVLC();
        _player = new MediaPlayer(_libVLC);
        _player.Playing += (_, _) => { AcquireWakeLock(); Playing?.Invoke(); };
        _player.Paused += (_, _) => { ReleaseWakeLock(); Paused?.Invoke(); };
        _player.Stopped += (_, _) => { ReleaseWakeLock(); Paused?.Invoke(); };
        _player.EndReached += (_, _) => { ReleaseWakeLock(); Ended?.Invoke(); };
    }

    public override IBinder? OnBind(Intent? intent) => _binder;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent == null) { StopSelf(); return StartCommandResult.NotSticky; }

        if (intent.Action == ActionStop)
        {
            StopPlayback();
            return StartCommandResult.NotSticky;
        }

        var filePath = intent.GetStringExtra("filePath") ?? "";
        if (string.IsNullOrEmpty(filePath))
        {
            // Started with StartForegroundService but nothing to play: still must not crash.
            EnterForeground();
            StopPlayback();
            return StartCommandResult.NotSticky;
        }

        var isNewTrack = filePath != FilePath;
        var restart = intent.GetBooleanExtra(ExtraRestart, true);

        FilePath = filePath;
        _title = intent.GetStringExtra("title") ?? "";
        _artist = intent.GetStringExtra("artist") ?? "";
        _openExtras = intent.Extras != null ? new Bundle(intent.Extras) : null;

        EnterForeground();   // must happen within a few seconds of StartForegroundService

        if (isNewTrack || restart)
        {
            PlayRegistered = false;
            PlayFile();
        }
        return StartCommandResult.NotSticky;
    }

    public void PlayFile()
    {
        if (_libVLC == null || _player == null || string.IsNullOrEmpty(FilePath)) return;
        EnterForeground();
        using var media = new Media(_libVLC, FilePath, FromType.FromPath);
        _player.Play(media);
    }

    public void StopPlayback()
    {
        _player?.Stop();
        ReleaseWakeLock();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void EnterForeground()
        => StartForeground(NotificationId, BuildNotification(), global::Android.Content.PM.ForegroundService.TypeMediaPlayback);

    private Notification BuildNotification()
    {
        var open = new Intent(this, typeof(PlayerActivity));
        if (_openExtras != null) open.PutExtras(_openExtras);
        open.PutExtra(ExtraFromNotification, true);
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentPi = PendingIntent.GetActivity(this, 0, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var stop = new Intent(this, typeof(PlaybackService)).SetAction(ActionStop);
        var stopPi = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(string.IsNullOrEmpty(_title) ? "MusicLib" : _title)
            .SetContentText(_artist)
            .SetContentIntent(contentPi)
            .SetOngoing(true)
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop", stopPi)
            .Build()!;
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock == null)
        {
            var pm = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "MusicLib:playback");
            _wakeLock.SetReferenceCounted(false);
        }
        if (!_wakeLock.IsHeld) _wakeLock.Acquire();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
    }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        _player?.Stop();
        _player?.Dispose();
        _libVLC?.Dispose();
        _player = null;
        _libVLC = null;
        base.OnDestroy();
    }
}
