using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Keeps <see cref="SlackInbox"/> on this PC only (never synced): one file encrypted for this Windows user (DPAPI),
/// since it holds the workspace's messages. Written a second after the last change, and at once on <see cref="Flush"/>.
/// A file that cannot be read (another user, a damaged file) starts SlackBot empty; Slack still has every message.
/// </summary>
public sealed class SlackInboxFile : IDisposable
{
    private const string Purpose = "helm.slackbot.inbox";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly ILogger<SlackInboxFile> _logger;
    private readonly Timer _timer;
    private int _dirty;

    public SlackInboxFile(HelmPaths paths, ISecretProtector protector, ILogger<SlackInboxFile> logger)
    {
        _path = Path.Combine(paths.ModuleDataDirectory(SlackBotModule.ModuleId), "inbox.bin");
        _protector = protector;
        _logger = logger;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Inbox = new SlackInbox(Load(), TimeProvider.System, Schedule);
    }

    public SlackInbox Inbox { get; }

    /// <summary>Writes a pending change now (on exit).</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        var bytes = Inbox.Export(data => JsonSerializer.SerializeToUtf8Bytes(data, Json));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(bytes, Purpose));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(ex, "Could not save SlackBot's messages");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }

    // Called under the inbox's lock: only start the timer.
    private void Schedule(InboxData data)
    {
        Volatile.Write(ref _dirty, 1);
        try
        {
            _timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private InboxData Load()
    {
        if (!File.Exists(_path)) return new InboxData();
        try
        {
            var bytes = _protector.Unprotect(File.ReadAllBytes(_path), Purpose);
            try
            {
                return JsonSerializer.Deserialize<InboxData>(bytes, Json) ?? new InboxData();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _logger.LogWarning(ex, "SlackBot's saved messages could not be read; starting empty");
            return new InboxData();
        }
    }
}
