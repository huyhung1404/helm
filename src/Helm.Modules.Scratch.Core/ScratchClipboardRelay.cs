using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Scratch;

/// <summary>"Put this on the clipboard": one record that every device watches (synced record in <c>scratch.clipboard</c>).</summary>
public sealed record ScratchClipboardSignal
{
    /// <summary>Scratch item to copy.</summary>
    public string ItemId { get; init; } = "";

    public DateTimeOffset SentAt { get; init; }

    /// <summary>The device name, for the message on the others.</summary>
    public string FromDevice { get; init; } = "";

    /// <summary>The sending install (<see cref="ScratchSettings.InstallId"/>): it does not copy its own signal again.</summary>
    public string Sender { get; init; } = "";
}

/// <summary>
/// Send to clipboard: the thing goes onto the clipboard of this device and of every other device that is syncing
/// (text as text; a file as a file, a photo also as a picture on Windows). Only a recent signal is followed, so a
/// device that comes back online later never overwrites its clipboard with something old. Each device can turn
/// receiving off (<see cref="ScratchSettings.ReceiveClipboard"/>).
/// </summary>
public sealed class ScratchClipboardRelay : IDisposable
{
    public const string Collection = "scratch.clipboard";

    /// <summary>The one record: the latest signal replaces the one before.</summary>
    public const string SignalId = "latest";

    /// <summary>A signal older than this when it arrives is ignored.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>Files larger than this are not copied on the other devices by themselves (the download would be long).</summary>
    public const long MaxAutoBytes = 100L * 1024 * 1024;

    private readonly ISyncedCollection<ScratchClipboardSignal> _signals;
    private readonly ScratchStore _store;
    private readonly IScratchPlatform _platform;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsStore<ScratchSettings> _settings;
    private readonly IDeviceInfo _device;
    private readonly IUiDispatcher _ui;
    private readonly ISyncService? _sync;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ScratchClipboardSignal? _waiting;
    private string? _handled;
    private bool _started;

    public ScratchClipboardRelay(ISyncedCollection<ScratchClipboardSignal> signals, ScratchStore store, IScratchPlatform platform, IClipboardService clipboard,
        ISettingsStoreFactory settings, IDeviceInfo device, IUiDispatcher ui, ISyncService? sync = null, ILogger<ScratchClipboardRelay>? logger = null,
        TimeProvider? time = null)
    {
        _signals = signals;
        _store = store;
        _platform = platform;
        _clipboard = clipboard;
        _settings = settings.Get<ScratchSettings>(ScratchIds.ModuleId);
        _device = device;
        _ui = ui;
        _sync = sync;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        if (_settings.Current.InstallId.Length == 0) _settings.Update(s => s.InstallId = Guid.NewGuid().ToString("N"));
    }

    /// <summary>Last writer wins: only the newest signal matters.</summary>
    public static SyncedCollectionOptions<ScratchClipboardSignal> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
    };

    /// <summary>Raised on the UI thread after something sent from another device was put on the clipboard (or could not be).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Starts following signals from the other devices (the module is on).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            // What was sent before this start is old news.
            _handled = _signals.Get(SignalId) is { } current ? Key(current) : null;
        }
        _signals.Changed += OnSignalsChanged;
        _store.Changed += OnStoreChanged;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            _waiting = null;
        }
        _signals.Changed -= OnSignalsChanged;
        _store.Changed -= OnStoreChanged;
    }

    /// <summary>Puts the thing on the clipboard here and asks the other devices to do the same.</summary>
    public void Send(string itemId)
    {
        var signal = new ScratchClipboardSignal
        {
            ItemId = itemId,
            SentAt = _time.GetUtcNow(),
            FromDevice = _device.DeviceName,
            Sender = _settings.Current.InstallId,
        };
        lock (_gate) _handled = Key(signal);
        _signals.Upsert(SignalId, signal);
        _sync?.RequestSync();
    }

    public void Dispose() => Stop();

    private void OnSignalsChanged(object? sender, SyncedChangedEventArgs e)
    {
        if (e.Origin != SyncChangeOrigin.Remote || _signals.Get(SignalId) is not { } signal) return;
        lock (_gate)
        {
            if (!_started || Key(signal) == _handled) return;
            _handled = Key(signal);
            if (signal.Sender == _settings.Current.InstallId || !_settings.Current.ReceiveClipboard) return;
            if (_time.GetUtcNow() - signal.SentAt > MaxAge) return;
            _waiting = signal;
        }
        TryHandleWaiting();
    }

    // The signal can arrive before the thing itself (a file is uploaded before its record): try again when it comes.
    private void OnStoreChanged(object? sender, SyncedChangedEventArgs e)
    {
        if (e.Origin == SyncChangeOrigin.Remote) TryHandleWaiting();
    }

    private void TryHandleWaiting()
    {
        ScratchClipboardSignal signal;
        ScratchItem item;
        lock (_gate)
        {
            if (_waiting is not { } waiting) return;
            if (_time.GetUtcNow() - waiting.SentAt > MaxAge)
            {
                _waiting = null;
                return;
            }
            if (_store.Get(waiting.ItemId) is not { } found) return;
            _waiting = null;
            signal = waiting;
            item = found;
        }
        _ui.Post(() => _ = CopyAsync(signal, item));
    }

    private async Task CopyAsync(ScratchClipboardSignal signal, ScratchItem item)
    {
        var from = signal.FromDevice.Length > 0 ? $" from {signal.FromDevice}" : "";
        try
        {
            if (item.Kind == ScratchKind.Text)
            {
                _clipboard.SetText(item.Text ?? "");
                Notice?.Invoke(this, $"Copied text{from} to the clipboard.");
                return;
            }
            if (item.Size > MaxAutoBytes && !_store.IsOnThisDevice(item))
            {
                Notice?.Invoke(this, $"“{item.Name}”{from} is {ScratchFormat.Size(item.Size)}: open Scratch and copy it from there.");
                return;
            }
            var file = await ExportAsync(signal.ItemId, item).ConfigureAwait(true);
            await _platform.CopyToClipboardAsync(file, CancellationToken.None).ConfigureAwait(true);
            Notice?.Invoke(this, $"Copied “{item.Name}”{from} to the clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy what another device sent to the clipboard");
            Notice?.Invoke(this, $"Could not copy “{item.DisplayName}”{from}: {ex.Message}");
        }
    }

    /// <summary>Copies this device's own thing to its clipboard (the sender, right away).</summary>
    public async Task CopyHereAsync(string itemId, ScratchItem item)
    {
        if (item.Kind == ScratchKind.Text)
        {
            _clipboard.SetText(item.Text ?? "");
            return;
        }
        await _platform.CopyToClipboardAsync(await ExportAsync(itemId, item).ConfigureAwait(true), CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>A decrypted copy in the open folder (the same place as Open and Share), reused while complete.</summary>
    internal async Task<ScratchLocalFile> ExportAsync(string id, ScratchItem item)
    {
        var name = ScratchFormat.SafeFileName(item.Name);
        var folder = Path.Combine(_platform.OpenFolder, id);
        var path = Path.Combine(folder, name);
        var file = new ScratchLocalFile(path, name, item.MediaType);
        if (File.Exists(path) && new FileInfo(path).Length == item.Size) return file;
        Directory.CreateDirectory(folder);
        var partial = path + ".part";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await _store.ReadAsync(item, stream).ConfigureAwait(true);
            File.Move(partial, path, overwrite: true);
            return file;
        }
        catch
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    private static string Key(ScratchClipboardSignal signal) => $"{signal.Sender}|{signal.ItemId}|{signal.SentAt.ToUnixTimeMilliseconds()}";
}
