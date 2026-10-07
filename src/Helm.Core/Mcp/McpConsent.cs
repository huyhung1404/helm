using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helm.Core.Settings;

namespace Helm.Core.Mcp;

/// <summary>What a tool call can do, and so whether the user is asked before it runs.</summary>
public enum McpRisk
{
    /// <summary>Only reads Helm's own data.</summary>
    Read,

    /// <summary>Changes Helm's data (synced to the other devices).</summary>
    Change,

    /// <summary>Runs something on another machine (SSH). Always goes through consent.</summary>
    Remote,
}

public enum McpDanger
{
    Normal,

    /// <summary>Shows the details prominently, and is never allowed for the rest of the session.</summary>
    High,
}

/// <summary>The question put to the user before one tool call runs.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="Title">"Run “Deploy” on server “web”".</param>
/// <param name="WhatItDoes">Plain words: what happens if it is allowed.</param>
/// <param name="WhyAsk">The risk: why Helm asks instead of just doing it.</param>
/// <param name="Details">The exact command line or arguments, shown as they are.</param>
/// <param name="Target">The server, list… the call acts on.</param>
/// <param name="Elevated">Helm runs as Administrator, or the remote user is root.</param>
public sealed record McpConsentRequest(
    string Tool,
    McpRisk Risk,
    string Title,
    string WhatItDoes,
    string WhyAsk,
    string? Details,
    string? Target,
    bool Elevated,
    McpDanger Danger);

/// <summary>Who is calling: one MCP connection, named by the client's <c>initialize</c>.</summary>
public sealed record McpClientInfo(Guid ConnectionId, string Name, string? Version)
{
    /// <summary>The caller's process runs as Administrator; null when unknown (not over the pipe, or its token could not be read).</summary>
    public bool? Elevated { get; init; }

    /// <summary>Helm runs as Administrator and this caller is known not to: allowing its calls lends it Helm's rights.</summary>
    public bool IsLessPrivilegedThan(bool helmElevated) => helmElevated && Elevated == false;
}

public enum McpConsentAnswer
{
    Deny,
    AllowOnce,
    AllowForSession,
}

/// <summary>Decides whether a tool call that is not <see cref="McpRisk.Read"/> may run (asking the user or not), and logs the answer.</summary>
public interface IMcpConsent
{
    /// <summary>Called by <see cref="McpServer"/> before the tool runs.</summary>
    Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct);
}

/// <summary>What the consent dialog shows for one call.</summary>
/// <param name="OfferSession">"Allow for this session" may be offered (never for <see cref="McpDanger.High"/>, nor for an elevated <see cref="McpRisk.Remote"/> call).</param>
/// <param name="HelmElevated">Helm runs as Administrator.</param>
/// <param name="CallerNotElevated">Helm runs as Administrator and the caller does not: allowing lends it Helm's rights.</param>
public sealed record McpConsentPrompt(McpConsentRequest Request, McpClientInfo Client, bool OfferSession, bool HelmElevated, bool CallerNotElevated);

/// <summary>Puts one question to the user (the dialog). Called one question at a time.</summary>
public interface IMcpConsentPrompt
{
    /// <summary>Waits for the answer. <paramref name="ct"/> is cancelled when the time is up or the client left: close and give up.</summary>
    Task<McpConsentAnswer> AskAsync(McpConsentPrompt prompt, CancellationToken ct);
}

/// <summary>Told how an allowed call ended; <see cref="McpServer"/> calls it when its consent implements it.</summary>
public interface IMcpCallObserver
{
    void Completed(McpConsentRequest request, McpClientInfo client, McpConsentAnswer answer, bool ok);
}

/// <summary>"Allow for this session": one connection, one tool, one target; forgotten when the connection ends or Helm restarts.</summary>
public sealed record McpSessionAllowance(Guid ConnectionId, string ClientName, string Tool, string? Target, string Title, DateTimeOffset Since);

/// <summary>
/// Helm's consent policy (see docs/mcp-security.md):
/// <list type="bullet">
/// <item><see cref="McpRisk.Read"/>: never asked.</item>
/// <item><see cref="McpRisk.Change"/>: asked when Helm runs as Administrator, the call is elevated, or the user turned on
/// "Ask before every change"; otherwise it runs (and is logged).</item>
/// <item><see cref="McpRisk.Remote"/>: always asked.</item>
/// </list>
/// One question at a time; no answer within <see cref="Timeout"/> is a Deny; more than <see cref="MaxAskedPerMinute"/>
/// asked calls in a minute are refused without asking. Every call it sees goes to the <see cref="McpActivityLog"/>.
/// </summary>
public sealed class McpConsentPolicy : IMcpConsent, IMcpCallObserver
{
    public const int MaxAskedPerMinute = 10;

    private const string AllowedWithoutAsking = "Allowed without asking";
    private const string AllowedForSession = "Allowed for this session";
    private readonly IMcpConsentPrompt _prompt;
    private readonly McpActivityLog _log;
    private readonly Func<bool> _askBeforeChanges;
    private readonly Func<bool> _helmElevated;
    private readonly TimeProvider _time;
    private readonly Action<string, string>? _notify;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _asked = new();
    private readonly List<McpSessionAllowance> _allowances = [];
    // How each allowed call was decided, until the server says how it ended (keyed by the request instance).
    private readonly ConditionalWeakTable<McpConsentRequest, string> _decided = new();

    /// <param name="askBeforeChanges">The user's "Ask before every change".</param>
    /// <param name="helmElevated">Helm runs as Administrator.</param>
    /// <param name="notify">A tray notification (title, message): shown after each allowed <see cref="McpRisk.Remote"/> call.</param>
    public McpConsentPolicy(IMcpConsentPrompt prompt, McpActivityLog log, Func<bool> askBeforeChanges, Func<bool> helmElevated,
        TimeProvider? time = null, Action<string, string>? notify = null)
    {
        _prompt = prompt;
        _log = log;
        _askBeforeChanges = askBeforeChanges;
        _helmElevated = helmElevated;
        _time = time ?? TimeProvider.System;
        _notify = notify;
    }

    /// <summary>How long a question waits for an answer before it counts as Deny.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public bool HelmElevated => _helmElevated();

    public McpActivityLog Log => _log;

    /// <summary>The "Allow for this session" answers still in force, oldest first.</summary>
    public IReadOnlyList<McpSessionAllowance> Allowances
    {
        get { lock (_gate) return _allowances.ToList(); }
    }

    /// <summary><see cref="Allowances"/> changed. Raised on the thread that changed them.</summary>
    public event EventHandler? AllowancesChanged;

    public void Revoke(McpSessionAllowance allowance)
    {
        bool removed;
        lock (_gate) removed = _allowances.Remove(allowance);
        if (removed) AllowancesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the allowances of connections that are gone.</summary>
    public void KeepConnections(IEnumerable<Guid> live)
    {
        var keep = live.ToHashSet();
        int removed;
        lock (_gate) removed = _allowances.RemoveAll(a => !keep.Contains(a.ConnectionId));
        if (removed > 0) AllowancesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct)
    {
        if (request.Risk == McpRisk.Read) return McpConsentAnswer.AllowOnce;
        var helmElevated = _helmElevated();
        var mustAsk = request.Risk == McpRisk.Remote || helmElevated || request.Elevated || _askBeforeChanges();
        if (!mustAsk) return Allowed(request, McpConsentAnswer.AllowOnce, AllowedWithoutAsking);

        // A change only touches Helm's own data, so Helm's elevation adds nothing to it: it may be allowed for the
        // session. Root or Administrator on another machine, or High danger, is asked every time.
        var offerSession = request.Danger == McpDanger.Normal && (request.Risk == McpRisk.Change || !request.Elevated);
        if (offerSession && HasAllowance(client, request)) return Allowed(request, McpConsentAnswer.AllowForSession, AllowedForSession);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            while (_asked.Count > 0 && now - _asked.Peek() >= TimeSpan.FromMinutes(1)) _asked.Dequeue();
            if (_asked.Count >= MaxAskedPerMinute)
            {
                Write(request, client, "Refused: too many questions", null);
                throw new McpToolException(
                    $"Helm did not ask the user: more than {MaxAskedPerMinute} calls needed their OK in the last minute. " +
                    "Wait a minute and do fewer things at once, or ask the user in the chat first.");
            }
            _asked.Enqueue(now);
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // An earlier question in the queue may have allowed this one for the session.
            if (offerSession && HasAllowance(client, request)) return Allowed(request, McpConsentAnswer.AllowForSession, AllowedForSession);
            var prompt = new McpConsentPrompt(request, client, offerSession, helmElevated, client.IsLessPrivilegedThan(helmElevated));
            McpConsentAnswer answer;
            using (var timeout = new CancellationTokenSource(Timeout, _time))
            using (var both = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                try
                {
                    answer = await _prompt.AskAsync(prompt, both.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Write(request, client, "Denied: no answer in time", null);
                    return McpConsentAnswer.Deny;
                }
                catch (OperationCanceledException)
                {
                    Write(request, client, "Denied: the client left", null);
                    throw;
                }
            }
            switch (answer)
            {
                case McpConsentAnswer.AllowForSession when offerSession:
                    lock (_gate)
                        _allowances.Add(new McpSessionAllowance(client.ConnectionId, client.Name, request.Tool, request.Target, request.Title, _time.GetLocalNow()));
                    AllowancesChanged?.Invoke(this, EventArgs.Empty);
                    return Allowed(request, McpConsentAnswer.AllowForSession, AllowedForSession);
                case McpConsentAnswer.AllowForSession:
                case McpConsentAnswer.AllowOnce:
                    return Allowed(request, McpConsentAnswer.AllowOnce, "Allowed once");
                default:
                    Write(request, client, "Denied", null);
                    return McpConsentAnswer.Deny;
            }
        }
        finally
        {
            _one.Release();
        }
    }

    public void Completed(McpConsentRequest request, McpClientInfo client, McpConsentAnswer answer, bool ok)
    {
        if (request.Risk == McpRisk.Read) return;
        var how = _decided.TryGetValue(request, out var decided) ? decided : answer == McpConsentAnswer.AllowForSession ? AllowedForSession : "Allowed";
        _decided.Remove(request);
        Write(request, client, how, ok);
        if (request.Risk == McpRisk.Remote)
            _notify?.Invoke(ok ? "An AI agent ran a command" : "An AI agent's command failed", $"{request.Title} ({client.Name})");
    }

    private McpConsentAnswer Allowed(McpConsentRequest request, McpConsentAnswer answer, string how)
    {
        _decided.AddOrUpdate(request, how);
        return answer;
    }

    private bool HasAllowance(McpClientInfo client, McpConsentRequest request)
    {
        lock (_gate)
            return _allowances.Exists(a => a.ConnectionId == client.ConnectionId && a.Tool == request.Tool && a.Target == request.Target);
    }

    private void Write(McpConsentRequest request, McpClientInfo client, string answer, bool? ok) =>
        _log.Add(new McpActivityEntry(_time.GetLocalNow(), client.Name, request.Tool, request.Risk, request.Title, request.Target, request.Details,
            answer, ok switch { true => "ok", false => "error", null => null }));
}

/// <summary>One call in the activity log. <see cref="Result"/> is null when the call did not run.</summary>
public sealed record McpActivityEntry(DateTimeOffset Time, string Client, string Tool, McpRisk Risk, string Title, string? Target,
    string? Details, string Answer, string? Result);

/// <summary>
/// The last <see cref="MaxEntries"/> calls that were not reads, in a small device-local JSON file (never synced).
/// Arguments appear only as the consent showed them (<see cref="McpConsentRequest.Details"/>, where secrets are already
/// hidden), cut to <see cref="MaxDetails"/> characters.
/// </summary>
public sealed class McpActivityLog
{
    public const int MaxEntries = 500;
    public const int MaxDetails = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string? _file;
    private readonly object _gate = new();
    private List<McpActivityEntry>? _entries;

    /// <param name="file">Null: kept in memory only.</param>
    public McpActivityLog(string? file) => _file = file;

    /// <summary>The log of a data folder: <c>mcp\activity.json</c>, next to (not inside) the synced data.</summary>
    public static string FileIn(HelmPaths paths) => Path.Combine(paths.Root, "mcp", "activity.json");

    /// <summary>Newest first.</summary>
    public IReadOnlyList<McpActivityEntry> Entries
    {
        get { lock (_gate) return Load().AsEnumerable().Reverse().ToList(); }
    }

    /// <summary>An entry was added, or the log was cleared. Raised on the thread that changed it.</summary>
    public event EventHandler? Changed;

    public void Add(McpActivityEntry entry)
    {
        if (entry.Details is { Length: > MaxDetails } details) entry = entry with { Details = details[..MaxDetails] + "…" };
        lock (_gate)
        {
            var entries = Load();
            entries.Add(entry);
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            Save(entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            try
            {
                if (_file is not null) File.Delete(_file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Save(_entries); // locked: empty it instead
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<McpActivityEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = _file is not null && File.Exists(_file) ? JsonSerializer.Deserialize<List<McpActivityEntry>>(File.ReadAllText(_file), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = []; // a damaged log starts again
        }
        return _entries;
    }

    private void Save(List<McpActivityEntry> entries)
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a convenience: a full disk must not stop the call.
        }
    }
}
