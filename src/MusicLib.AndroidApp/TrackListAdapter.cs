using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using MusicLib.Core;

namespace MusicLib.AndroidApp;

/// <summary>One row's worth of display data — a Track plus its merged (all-devices) state.</summary>
public sealed record TrackRow(Track Track, EffectiveState? State)
{
    // Tag overrides (synced from other devices) win over what the file itself says.
    public string Title => string.IsNullOrEmpty(State?.Title) ? Track.Title : State!.Title!;
    public string Artist => string.IsNullOrEmpty(State?.Artist) ? Track.Artist : State!.Artist!;
}

public sealed class TrackListAdapter : RecyclerView.Adapter
{
    public event Action<TrackRow>? TrackClicked;

    private List<TrackRow> _rows = new();
    private string? _selectedHash;

    public void Submit(List<TrackRow> rows)
    {
        _rows = rows;
        NotifyDataSetChanged();
    }

    /// <summary>Hash of the track shown as selected (highlighted); null = none.</summary>
    public string? SelectedHash
    {
        get => _selectedHash;
        set
        {
            if (_selectedHash == value) return;
            _selectedHash = value;
            NotifyDataSetChanged();
        }
    }

    public override int ItemCount => _rows.Count;

    public sealed class RowHolder : RecyclerView.ViewHolder
    {
        public required TextView Title { get; init; }
        public required TextView Sub { get; init; }
        public RowHolder(View root) : base(root) { }
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var context = parent.Context!;
        var row = new LinearLayout(context) { Orientation = Orientation.Vertical };
        row.SetPadding(24, 20, 24, 20);
        row.LayoutParameters = new RecyclerView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        var title = new TextView(context) { TextSize = 16 };
        var sub = new TextView(context) { TextSize = 12 };
        row.AddView(title);
        row.AddView(sub);

        var holder = new RowHolder(row) { Title = title, Sub = sub };

        row.Click += (_, _) =>
        {
            var pos = holder.BindingAdapterPosition;
            if (pos != RecyclerView.NoPosition) TrackClicked?.Invoke(_rows[pos]);
        };

        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder viewHolder, int position)
    {
        var row = _rows[position];
        var holder = (RowHolder)viewHolder;

        holder.Title.Text = row.Title;
        holder.Sub.Text = row.Artist;
        holder.Sub.Visibility = string.IsNullOrEmpty(row.Artist) ? ViewStates.Gone : ViewStates.Visible;

        holder.ItemView.SetBackgroundColor(
            row.Track.Hash == _selectedHash ? Color.Argb(48, 0, 150, 136) : Color.Transparent);
    }
}
