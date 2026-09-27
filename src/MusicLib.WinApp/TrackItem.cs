using MusicLib.Core;

namespace MusicLib.WinApp;

/// <summary>Plain display wrapper for x:Bind — not INotifyPropertyChanged; the
/// list is simply rebuilt and reassigned whenever state changes (same approach
/// as the Android adapter's Submit/NotifyDataSetChanged).</summary>
public sealed class TrackItem
{
    public required Track Track { get; init; }
    public EffectiveState? State { get; init; }

    public string Star => State?.IsFavorite == true ? "\u2605" : "\u2606";

    public string Display
    {
        get
        {
            var artist = string.IsNullOrEmpty(State?.Artist) ? Track.Artist : State!.Artist;
            var title = string.IsNullOrEmpty(State?.Title) ? Track.Title : State!.Title;
            return string.IsNullOrEmpty(artist) ? title! : $"{artist} - {title}";
        }
    }

    public string Sub => $"{State?.PlayCount ?? 0}x played  \u00b7  {Track.RelPath}";
}
