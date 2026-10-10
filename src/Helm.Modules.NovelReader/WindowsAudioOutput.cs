using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Plays reading aloud through one Windows <see cref="MediaPlayer"/>, so it shows in the system media controls (the
/// volume flyout and the lock screen, with the novel and chapter) and the media keys and headset buttons drive it.
/// Pause stops in the middle of a sentence.
/// </summary>
public sealed class WindowsAudioOutput(ILogger<WindowsAudioOutput> logger) : IAudioOutput, IDisposable
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private int _playId;
    private double _volume = 1;

    public event EventHandler<MediaButton>? MediaButton;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_gate)
                if (_player is not null) _player.Volume = _volume;
        }
    }

    public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(audio.Data);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }
        stream.Seek(0);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer player;
        int id;
        lock (_gate)
        {
            player = Player();
            id = ++_playId;
        }
        void Ended(MediaPlayer sender, object args) => done.TrySetResult();
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => done.TrySetException(new InvalidOperationException(
            args.ErrorMessage is { Length: > 0 } message ? message : "The sound could not be played."));
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
        using var registration = ct.Register(() =>
        {
            lock (_gate)
            {
                // A newer sentence may already own the player; only stop our own.
                if (_playId == id) Try(player.Pause);
            }
            done.TrySetCanceled(ct);
        });
        try
        {
            lock (_gate)
            {
                player.Volume = _volume;
                player.Source = MediaSource.CreateFromStream(stream, audio.ContentType);
                player.Play();
            }
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            player.MediaEnded -= Ended;
            player.MediaFailed -= Failed;
            stream.Dispose();
        }
    }

    public void Pause()
    {
        lock (_gate) Try(() => _player?.Pause());
    }

    public void Resume()
    {
        lock (_gate) Try(() => _player?.Play());
    }

    public void ShowNowPlaying(string title, string subtitle)
    {
        lock (_gate)
        {
            try
            {
                var updater = Player().SystemMediaTransportControls.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = subtitle;
                updater.MusicProperties.Artist = title;
                updater.MusicProperties.AlbumTitle = "Novel Reader";
                updater.Update();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not update the media controls");
            }
        }
    }

    public void ClearNowPlaying()
    {
        lock (_gate)
        {
            if (_player is null) return;
            Try(() =>
            {
                _player.Pause();
                _player.Source = null;
                _player.SystemMediaTransportControls.DisplayUpdater.ClearAll();
                _player.SystemMediaTransportControls.DisplayUpdater.Update();
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _player?.Dispose();
            _player = null;
        }
    }

    /// <summary>One player for the whole session, so the media keys keep working between sentences.</summary>
    private MediaPlayer Player()
    {
        if (_player is not null) return _player;
        var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        var commands = player.CommandManager;
        commands.IsEnabled = true;
        commands.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        // Reading aloud decides what a button does (the player alone would only pause the current sentence).
        commands.PlayReceived += (_, e) => { e.Handled = true; Raise(Speech.MediaButton.Play); };
        commands.PauseReceived += (_, e) => { e.Handled = true; Raise(Speech.MediaButton.Pause); };
        commands.NextReceived += (_, e) => { e.Handled = true; Raise(Speech.MediaButton.Next); };
        commands.PreviousReceived += (_, e) => { e.Handled = true; Raise(Speech.MediaButton.Previous); };
        _player = player;
        return player;
    }

    private void Raise(MediaButton button) => MediaButton?.Invoke(this, button);

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Media player call failed");
        }
    }
}
