using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using MusicLib.Core;

namespace MusicLib.AndroidApp;

/// <summary>One row's worth of display data — a Track plus its merged (all-devices) state.</summary>
public sealed record TrackRow(Track Track, EffectiveState? State);

public sealed class TrackListAdapter : RecyclerView.Adapter
{
    public event Action<Track>? TrackClicked;
    public event Action<Track>? FavoriteToggled;

    private List<TrackRow> _rows = new();

    public void Submit(List<TrackRow> rows)
    {
        _rows = rows;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _rows.Count;

    public sealed class RowHolder : RecyclerView.ViewHolder
    {
        public required TextView Star { get; init; }
        public required TextView Title { get; init; }
        public required TextView Sub { get; init; }
        public RowHolder(View root) : base(root) { }
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var context = parent.Context!;
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetPadding(24, 20, 24, 20);

        var star = new TextView(context) { TextSize = 20 };
        star.SetPadding(0, 0, 24, 0);
        row.AddView(star, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var textCol = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var title = new TextView(context) { TextSize = 16 };
        var sub = new TextView(context) { TextSize = 12 };
        textCol.AddView(title);
        textCol.AddView(sub);
        row.AddView(textCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var holder = new RowHolder(row) { Star = star, Title = title, Sub = sub };

        row.Click += (_, _) =>
        {
            var pos = holder.BindingAdapterPosition;
            if (pos != RecyclerView.NoPosition) TrackClicked?.Invoke(_rows[pos].Track);
        };
        star.Click += (_, _) =>
        {
            var pos = holder.BindingAdapterPosition;
            if (pos != RecyclerView.NoPosition) FavoriteToggled?.Invoke(_rows[pos].Track);
        };

        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder viewHolder, int position)
    {
        var (track, state) = _rows[position];
        var holder = (RowHolder)viewHolder;

        var artist = string.IsNullOrEmpty(state?.Artist) ? track.Artist : state.Artist;
        var title = string.IsNullOrEmpty(state?.Title) ? track.Title : state.Title;

        holder.Star.Text = state?.IsFavorite == true ? "\u2605" : "\u2606"; // ★ / ☆
        holder.Title.Text = string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}";
        holder.Sub.Text = $"{state?.PlayCount ?? 0}x played  \u00b7  {track.RelPath}";
    }
}
