using Android.Content;
using Android.Media;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.NovelReader.Playback;

/// <summary>What the media notification and the lock screen show.</summary>
public sealed record NowPlaying(string Title, string Subtitle, bool IsPaused, byte[]? Artwork);

/// <summary>
/// Reading aloud on the phone: one MediaPlayer plays the sentences straight from memory, audio focus pauses it for a
/// call or another app playing (and goes on afterwards), and <see cref="NovelReaderPlaybackService"/> keeps it reading
/// with the screen off, with the media notification, the lock screen and headset buttons. ReadAloudController calls it
/// on the UI (main) thread; the player reports on the main thread too.
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private readonly ILogger<AndroidAudioOutput> _logger;
    private readonly AudioAttributes _attributes = new AudioAttributes.Builder()
        .SetUsage(AudioUsageKind.Media)!
        .SetContentType(AudioContentType.Speech)!
        .Build()!;
    private MediaPlayer? _player;
    private TaskCompletionSource? _playing;
    private bool _prepared;
    private bool _paused;
    private double _volume = 1;
    private AudioFocusRequestClass? _focus;
    private bool _pausedForFocus;
    private bool _serviceStarting;

    public AndroidAudioOutput(ILogger<AndroidAudioOutput> logger)
    {
        _logger = logger;
    }

    private static Context Context => AndroidApp.Context;

    /// <summary>The novel's cover for the notification (set by the module; read on the UI thread).</summary>
    public Func<byte[]?>? Artwork { get; set; }

    /// <summary>What is being read, or null when reading aloud is stopped.</summary>
    public NowPlaying? Current { get; private set; }

    /// <summary>Reading started, paused, resumed, moved to another chapter or stopped (main thread).</summary>
    public event EventHandler? Changed;

    public event EventHandler<MediaButton>? MediaButton;

    /// <summary>The notification, the lock screen, a headset or the phone (a call) pressed a button.</summary>
    internal void Press(MediaButton button) => MediaButton?.Invoke(this, button);

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            try
            {
                _player?.SetVolume((float)_volume, (float)_volume);
            }
            catch (Java.Lang.IllegalStateException)
            {
            }
        }
    }

    public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        _playing?.TrySetCanceled();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _playing = done;
        var player = _player ??= CreatePlayer();
        _prepared = false;
        try
        {
            player.Reset();
            player.SetAudioAttributes(_attributes);
            player.SetDataSource(new MemoryMediaSource(audio.Data));
            player.PrepareAsync();
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.IO.IOException or ArgumentException)
        {
            // A player in a bad state: start over with a new one next time, and skip this sentence.
            _logger.LogWarning(ex, "Could not play a sentence");
            ReleasePlayer();
            return;
        }
        using (ct.Register(() => StopIfCurrent(done)))
            await done.Task.ConfigureAwait(true);
    }

    public void Pause()
    {
        _paused = true;
        try
        {
            if (_prepared && _player is { IsPlaying: true } player) player.Pause();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
        Update();
    }

    public void Resume()
    {
        _paused = false;
        _pausedForFocus = false;
        RequestFocus();
        try
        {
            if (_prepared && _player is { IsPlaying: false } player && _playing is { Task.IsCompleted: false }) player.Start();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
        Update();
    }

    public void ShowNowPlaying(string title, string subtitle)
    {
        RequestFocus();
        var artwork = Artwork?.Invoke();
        var now = new NowPlaying(title, subtitle, _paused, artwork);
        var changed = Current is not { } old || old.Title != title || old.Subtitle != subtitle || old.IsPaused != _paused || !ReferenceEquals(old.Artwork, artwork);
        Current = now;
        if (!NovelReaderPlaybackService.IsRunning) StartService();
        // Every sentence: the service keeps the phone awake a while longer.
        Changed?.Invoke(this, changed ? EventArgs.Empty : KeepAwake);
    }

    public void ClearNowPlaying()
    {
        Current = null;
        _paused = false;
        _pausedForFocus = false;
        _serviceStarting = false;
        AbandonFocus();
        ReleasePlayer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised with <see cref="Changed"/> when only the time to keep awake moved on (nothing to redraw).</summary>
    internal static EventArgs KeepAwake { get; } = new();

    /// <summary>Called by the service once it runs.</summary>
    internal void ServiceStarted() => _serviceStarting = false;

    private void Update()
    {
        if (Current is not { } now) return;
        Current = now with { IsPaused = _paused };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartService()
    {
        if (_serviceStarting) return;
        _serviceStarting = true;
        try
        {
            var intent = new Intent(Context, typeof(NovelReaderPlaybackService)).SetAction(NovelReaderPlaybackService.ActionShow)!;
            Context.StartForegroundService(intent);
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            // Android 12+ refuses to start it from the background (should not happen: reading starts from Helm or its notification).
            _serviceStarting = false;
            _logger.LogWarning(ex, "Could not start reading aloud in the background");
        }
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer();
        // The CPU stays awake while a sentence plays with the screen off (the service covers the gaps between them).
        player.SetWakeMode(Context, Android.OS.WakeLockFlags.Partial);
        player.Prepared += (_, _) =>
        {
            _prepared = true;
            try
            {
                player.SetVolume((float)_volume, (float)_volume);
                if (!_paused) player.Start();
            }
            catch (Java.Lang.IllegalStateException ex)
            {
                _logger.LogWarning(ex, "Could not start a sentence");
                _playing?.TrySetResult();
            }
        };
        player.Completion += (_, _) => _playing?.TrySetResult();
        player.Error += (_, e) =>
        {
            // A sentence that cannot be played is skipped; reading goes on.
            _logger.LogWarning("The player could not play a sentence ({What}, {Extra})", e.What, e.Extra);
            e.Handled = true;
            _playing?.TrySetResult();
        };
        return player;
    }

    private void StopIfCurrent(TaskCompletionSource done)
    {
        if (!done.TrySetCanceled()) return;
        if (!ReferenceEquals(_playing, done)) return;
        _prepared = false;
        try
        {
            _player?.Reset();
        }
        catch (Java.Lang.IllegalStateException)
        {
            ReleasePlayer();
        }
    }

    private void ReleasePlayer()
    {
        _playing?.TrySetCanceled();
        _prepared = false;
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

    // ---- Audio focus: a call or another app playing pauses reading; it goes on when they are done ------------------

    private void RequestFocus()
    {
        if (_focus is not null || Context.GetSystemService(Context.AudioService) is not AudioManager audio) return;
        var request = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(_attributes)!
            // Speech is not lowered under a notification sound: it pauses, so no words are missed.
            .SetWillPauseWhenDucked(true)!
            .SetOnAudioFocusChangeListener(new FocusListener(this))!
            .Build()!;
        if (audio.RequestAudioFocus(request) == AudioFocusRequest.Granted) _focus = request;
    }

    private void AbandonFocus()
    {
        if (_focus is { } focus && Context.GetSystemService(Context.AudioService) is AudioManager audio) audio.AbandonAudioFocusRequest(focus);
        _focus = null;
    }

    private void OnFocusChange(AudioFocus change)
    {
        switch (change)
        {
            case AudioFocus.Loss:
                // Another app plays for good (music): pause, and stay paused.
                _focus = null;
                _pausedForFocus = false;
                if (Current is not null && !_paused) Press(Speech.MediaButton.Pause);
                break;
            case AudioFocus.LossTransient:
            case AudioFocus.LossTransientCanDuck:
                // A call, a navigation prompt: pause, and go on when it is over.
                if (Current is not null && !_paused)
                {
                    _pausedForFocus = true;
                    Press(Speech.MediaButton.Pause);
                }
                break;
            case AudioFocus.Gain:
                if (_pausedForFocus)
                {
                    _pausedForFocus = false;
                    Press(Speech.MediaButton.Play);
                }
                break;
        }
    }

    private sealed class FocusListener(AndroidAudioOutput output) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) => output.OnFocusChange(focusChange);
    }

    /// <summary>A sentence's sound (MP3 or WAV) handed to the player from memory.</summary>
    private sealed class MemoryMediaSource(byte[] data) : MediaDataSource
    {
        public override long Size => data.Length;

        public override int ReadAt(long position, byte[]? buffer, int offset, int size)
        {
            if (buffer is null || position >= data.Length) return -1;
            var count = (int)Math.Min(size, data.Length - position);
            Array.Copy(data, position, buffer, offset, count);
            return count;
        }

        public override void Close()
        {
        }
    }
}
