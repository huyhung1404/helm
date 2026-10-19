using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Plays a word said aloud through a <see cref="MediaPlayer"/> of its own, kept out of the system media controls (those
/// stay with reading aloud, <see cref="WindowsAudioOutput"/>). A newer clip cuts the one playing.
/// </summary>
public sealed class WindowsClipPlayer(ILogger<WindowsClipPlayer> logger) : IClipPlayer, IDisposable
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private int _playId;

    public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(audio.Data);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }
        stream.Seek(0);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer player;
        int id;
        lock (_gate)
        {
            player = _player ??= new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
            player.CommandManager.IsEnabled = false;
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
                // A newer clip may already own the player; only stop our own.
                if (_playId == id) Try(player.Pause);
            }
            done.TrySetCanceled(ct);
        });
        try
        {
            lock (_gate)
            {
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

    public void Dispose()
    {
        lock (_gate)
        {
            _player?.Dispose();
            _player = null;
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Clip player call failed");
        }
    }
}
