using MusicLib.Core;

namespace MusicLib.WinApp;

/// <summary>Plain display wrapper for x:Bind — not INotifyPropertyChanged; the
/// list is simply rebuilt and reassigned whenever state changes (same approach
/// as the Android adapter's Submit/NotifyDataSetChanged).</summary>
public sealed class TrackItem
{
    public required Track Track { get; init; }
    public EffectiveState? State { get; init; }

    // Tag overrides (synced from other devices) win over what the file itself says.
    public string Title => string.IsNullOrEmpty(State?.Title) ? Track.Title : State!.Title!;
    public string Artist => string.IsNullOrEmpty(State?.Artist) ? Track.Artist : State!.Artist!;
}
