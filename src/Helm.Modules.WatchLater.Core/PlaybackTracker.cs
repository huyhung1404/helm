namespace Helm.Modules.WatchLater;

/// <summary>What a player can play in Helm, and plays it (Windows: a WebView2 window; Android has none).</summary>
public interface IVideoPlayer
{
    /// <summary>True when the video can be played in Helm (the site allows embedding it, or it is downloaded here).</summary>
    bool CanPlay(WatchItem item);

    /// <summary>Plays the video, carrying on where watching stopped.</summary>
    void Play(string id);
}

/// <summary>
/// One playback's progress, written to the store sparingly: a synced write every <see cref="SaveEvery"/> of watching
/// (and when paused or closed) is enough to carry on elsewhere, without a sync on every tick of the player.
/// </summary>
public sealed class PlaybackTracker(WatchLaterStore store, string id)
{
    public const double SaveEvery = 15;

    private double _saved = double.NaN;
    private double _position;
    private double _duration;
    private bool _watched;

    public string Id { get; } = id;

    /// <summary>Raised once, when watching passes <see cref="WatchLaterStore.WatchedShare"/> of the video.</summary>
    public event EventHandler? MarkedWatched;

    /// <summary>The player's position and length, a few times a minute or more.</summary>
    public void Report(double position, double duration)
    {
        if (double.IsNaN(position) || position < 0) return;
        _position = position;
        if (duration > 0) _duration = duration;
        var crossed = !_watched && _duration > 0 && position >= _duration * WatchLaterStore.WatchedShare;
        if (crossed || double.IsNaN(_saved) || Math.Abs(position - _saved) >= SaveEvery) Save();
    }

    /// <summary>Paused, ended, closed or another video: write where it is now.</summary>
    public void Flush()
    {
        if (_position > 0 && (double.IsNaN(_saved) || Math.Abs(_position - _saved) >= 1)) Save();
    }

    private void Save()
    {
        _saved = _position;
        // Once watched, carrying on (or seeking back) no longer brings a resume position back.
        if (_watched) return;
        if (store.SaveProgress(Id, _position, _duration))
        {
            _watched = true;
            MarkedWatched?.Invoke(this, EventArgs.Empty);
        }
        else if (store.Get(Id) is { Watched: true })
        {
            _watched = true;
        }
    }
}
