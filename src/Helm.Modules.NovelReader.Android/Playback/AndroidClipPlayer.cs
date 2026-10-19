using Android.Content;
using Android.Media;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.NovelReader.Playback;

/// <summary>
/// Plays a word said aloud through a MediaPlayer of its own: no notification and no media session (those stay with
/// reading aloud, <see cref="AndroidAudioOutput"/>). Music of another app is lowered under it for a moment. A newer clip
/// cuts the one playing. Called on the main thread.
/// </summary>
public sealed class AndroidClipPlayer(ILogger<AndroidClipPlayer> logger) : IClipPlayer
{
    private readonly AudioAttributes _attributes = new AudioAttributes.Builder()
        .SetUsage(AudioUsageKind.Media)!
        .SetContentType(AudioContentType.Speech)!
        .Build()!;
    private MediaPlayer? _player;
    private TaskCompletionSource? _playing;
    private AudioFocusRequestClass? _focus;

    public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _playing?.TrySetCanceled();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _playing = done;
        var player = _player ??= CreatePlayer();
        try
        {
            player.Reset();
            player.SetAudioAttributes(_attributes);
            player.SetDataSource(new AndroidAudioOutput.MemoryMediaSource(audio.Data));
            RequestFocus();
            player.PrepareAsync();
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.IO.IOException or ArgumentException)
        {
            // A player in a bad state: a new one next time.
            logger.LogWarning(ex, "Could not play a word");
            Release();
            throw new InvalidOperationException("The sound could not be played.", ex);
        }
        using (ct.Register(() => Stop(done)))
        {
            try
            {
                await done.Task.ConfigureAwait(true);
            }
            finally
            {
                if (ReferenceEquals(_playing, done))
                {
                    _playing = null;
                    AbandonFocus();
                }
            }
        }
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer();
        player.Prepared += (_, _) =>
        {
            if (_playing is null) return;
            try
            {
                player.Start();
            }
            catch (Java.Lang.IllegalStateException ex)
            {
                _playing?.TrySetException(ex);
            }
        };
        player.Completion += (_, _) => _playing?.TrySetResult();
        player.Error += (_, e) =>
        {
            e.Handled = true;
            _playing?.TrySetException(new InvalidOperationException($"The sound could not be played ({e.What})."));
            Release();
        };
        return player;
    }

    /// <summary>Stops a clip that was cancelled, unless a newer one plays already.</summary>
    private void Stop(TaskCompletionSource done)
    {
        if (!ReferenceEquals(_playing, done))
        {
            done.TrySetCanceled();
            return;
        }
        try
        {
            _player?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
        done.TrySetCanceled();
    }

    private void Release()
    {
        var player = _player;
        _player = null;
        try
        {
            player?.Release();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
        player?.Dispose();
    }

    private void RequestFocus()
    {
        if (_focus is not null || AndroidApp.Context.GetSystemService(Context.AudioService) is not AudioManager audio) return;
        var request = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)
            .SetAudioAttributes(_attributes)!
            .Build()!;
        if (audio.RequestAudioFocus(request) == AudioFocusRequest.Granted) _focus = request;
    }

    private void AbandonFocus()
    {
        if (_focus is { } focus && AndroidApp.Context.GetSystemService(Context.AudioService) is AudioManager audio) audio.AbandonAudioFocusRequest(focus);
        _focus = null;
    }
}
