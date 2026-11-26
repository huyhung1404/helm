using Helm.Core.Diagnostics;
using Helm.Core.Sync;

namespace Helm.Shell.BugReports;

/// <summary>Where the pictures and black boxes of bug reports are kept: the sync blob store (end-to-end encrypted).</summary>
public interface IBugFiles
{
    Task<BlobRef> SaveAsync(byte[] data, CancellationToken ct = default);

    Task<byte[]> ReadAsync(BlobRef blob, CancellationToken ct = default);
}

/// <summary><see cref="IBugFiles"/> on Helm Sync's <see cref="BlobStore"/>.</summary>
public sealed class BlobBugFiles(BlobStore blobs) : IBugFiles
{
    public async Task<BlobRef> SaveAsync(byte[] data, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data, writable: false);
        return await blobs.ImportAsync(stream, null, ct).ConfigureAwait(false);
    }

    public Task<byte[]> ReadAsync(BlobRef blob, CancellationToken ct = default) => blobs.ReadAllAsync(blob, ct);
}

/// <summary>A new bug, as the report window or a crash hands it over.</summary>
public sealed record BugDraft(
    string Text,
    BugSeverity Severity,
    string Tool,
    IReadOnlyList<BugImageDraft> Images,
    BlackBoxSnapshot? BlackBox,
    bool FromCrash = false);

/// <summary>What a change did, so it can be taken back (an agent's Undo): the report before it and the entry it added.</summary>
public sealed record BugChange(string BugId, BugReport Before, BugReport After, string? EntryId);

/// <summary>
/// The user's bug reports and their conversations, synced across their devices: reported here, from the phone, or
/// from a crash; worked on by AI agents over MCP (<see cref="BugMcpTools"/>) and closed only by the user. Writes are
/// local and immediate.
/// </summary>
public sealed class BugStore
{
    public const string ReportsCollection = "bugs.reports";
    public const string EntriesCollection = "bugs.entries";
    public const string SyncPrefix = "bugs.";

    /// <summary>How the user is named in a bug's conversation.</summary>
    public const string You = "You";

    public const int MaxTitle = 120;
    public const int MaxImages = 10;
    public const int MaxImageBytes = 15 * 1024 * 1024;

    private readonly ISyncedCollection<BugReport> _reports;
    private readonly ISyncedCollection<BugEntry> _entries;
    private readonly IBugFiles _files;
    private readonly TimeProvider _time;
    private readonly Func<string> _deviceName;
    private readonly object _gate = new();

    public BugStore(ISyncedCollection<BugReport> reports, ISyncedCollection<BugEntry> entries, IBugFiles files,
        Func<string> deviceName, BugDeviceKind deviceKind, string appVersion, TimeProvider? time = null)
    {
        _reports = reports;
        _entries = entries;
        _files = files;
        _deviceName = deviceName;
        DeviceKind = deviceKind;
        AppVersion = appVersion;
        _time = time ?? TimeProvider.System;
        _reports.Changed += (_, e) => Changed?.Invoke(this, e);
        _entries.Changed += (_, e) => Changed?.Invoke(this, e);
    }

    public BugDeviceKind DeviceKind { get; }

    public string AppVersion { get; }

    /// <summary>A bug or an entry changed, here or by sync (any thread).</summary>
    public event EventHandler<SyncedChangedEventArgs>? Changed;

    /// <summary>The options of the entries' collection (they name their blobs, so the blobs sync first).</summary>
    public static SyncedCollectionOptions<BugEntry> EntryOptions { get; } = new()
    {
        Name = EntriesCollection,
        ConflictPolicy = SyncConflictPolicy.RemoteWins,
        BlobReferences = BlobsOf,
    };

    public static SyncedCollectionOptions<BugReport> ReportOptions { get; } = new() { Name = ReportsCollection };

    public static IEnumerable<string> BlobsOf(BugEntry entry) =>
        entry.Images.Select(i => i.Blob.Id).Concat(entry.BlackBox is { } box ? [box.Id] : []);

    // ---- reading ----

    /// <summary>Every bug, newest first.</summary>
    public IReadOnlyList<SyncedItem<BugReport>> All() =>
        [.. _reports.All().OrderByDescending(r => r.Value.CreatedAt).ThenByDescending(r => r.Value.Number)];

    public BugReport? Get(string id) => _reports.Get(id);

    /// <summary>A bug by its id, or by its number as people write it: "B12", "#B12", "12".</summary>
    public SyncedItem<BugReport>? Find(string idOrNumber)
    {
        var key = idOrNumber.Trim();
        if (_reports.Get(key) is not null) return _reports.All().FirstOrDefault(r => r.Id == key);
        var number = key.TrimStart('#');
        if (number.StartsWith('B') || number.StartsWith('b')) number = number[1..];
        if (!int.TryParse(number, out var n)) return null;
        // Two devices offline may give the same number: the newer report wins the name.
        return _reports.All().Where(r => r.Value.Number == n).OrderByDescending(r => r.Value.CreatedAt).FirstOrDefault();
    }

    /// <summary>A bug's conversation, oldest first.</summary>
    public IReadOnlyList<SyncedItem<BugEntry>> Entries(string bugId) =>
        [.. _entries.All().Where(e => e.Value.BugId == bugId).OrderBy(e => e.Value.At).ThenBy(e => e.Id, StringComparer.Ordinal)];

    public BugEntry? GetEntry(string id) => _entries.Get(id);

    /// <summary>The newest black box of a bug (a reopen or added details may carry a newer one than the report).</summary>
    public SyncedItem<BugEntry>? LatestBlackBox(string bugId) => Entries(bugId).LastOrDefault(e => e.Value.BlackBox is not null);

    public async Task<BlackBoxSnapshot?> ReadBlackBoxAsync(BugEntry entry, CancellationToken ct = default) =>
        entry.BlackBox is { } blob ? BlackBoxSnapshot.FromBytes(await _files.ReadAsync(blob, ct).ConfigureAwait(false)) : null;

    public Task<byte[]> ReadImageAsync(BugAttachment image, CancellationToken ct = default) => _files.ReadAsync(image.Blob, ct);

    /// <summary>Bugs not closed.</summary>
    public int OpenCount => _reports.All().Count(r => r.Value.Status != BugStatus.Closed);

    /// <summary>Bugs waiting for the user to check whose fix is in <paramref name="version"/> (or older).</summary>
    public IReadOnlyList<SyncedItem<BugReport>> FixedIn(string version) =>
        [.. All().Where(r => r.Value.Status == BugStatus.ToVerify && r.Value.FixedIn is { } v && CompareVersions(v, version) <= 0)];

    // ---- the user ----

    /// <summary>Saves a new bug with its pictures and black box. Returns its id.</summary>
    public async Task<string> ReportAsync(BugDraft draft, CancellationToken ct = default)
    {
        var text = draft.Text.Trim();
        if (text.Length == 0) throw new ArgumentException("Write what happened.");
        var attachments = await SaveAttachmentsAsync(draft.Images, draft.BlackBox, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        string id;
        lock (_gate)
        {
            var report = new BugReport
            {
                Number = NextNumber(),
                Title = TitleOf(text),
                Description = text,
                Severity = draft.Severity,
                Tool = string.IsNullOrWhiteSpace(draft.Tool) ? "Helm" : draft.Tool.Trim(),
                Device = _deviceName(),
                DeviceKind = DeviceKind,
                AppVersion = AppVersion,
                CreatedAt = now,
                Status = BugStatus.Open,
                StatusChangedAt = now,
                FromCrash = draft.FromCrash,
            };
            id = _reports.Add(report);
        }
        AddEntry(id, BugEntryKind.Report, You, false, _deviceName(), text, attachments);
        return id;
    }

    /// <summary>The user adds details (words, pictures, a new black box).</summary>
    public async Task<string> AddDetailsAsync(string bugId, string text, IReadOnlyList<BugImageDraft> images, BlackBoxSnapshot? blackBox, CancellationToken ct = default)
    {
        Require(bugId);
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0 && blackBox is null) throw new ArgumentException("Nothing to add.");
        var attachments = await SaveAttachmentsAsync(images, blackBox, ct).ConfigureAwait(false);
        return AddEntry(bugId, BugEntryKind.Details, You, false, _deviceName(), text.Trim(), attachments);
    }

    /// <summary>The user answers the agent's pending question.</summary>
    public async Task<string> AnswerAsync(string bugId, string text, IReadOnlyList<BugImageDraft> images, BlackBoxSnapshot? blackBox, CancellationToken ct = default)
    {
        Require(bugId);
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) throw new ArgumentException("Write an answer.");
        var attachments = await SaveAttachmentsAsync(images, blackBox, ct).ConfigureAwait(false);
        var entry = AddEntry(bugId, BugEntryKind.Answer, You, false, _deviceName(), text.Trim(), attachments);
        Update(bugId, r => r with { PendingQuestion = null });
        return entry;
    }

    /// <summary>Closes the bug (only the user does).</summary>
    public BugChange Close(string bugId, BugCloseReason reason, string? note = null, string? duplicateOf = null)
    {
        var text = reason switch
        {
            BugCloseReason.Fixed => "Closed: fixed",
            BugCloseReason.NotABug => "Closed: not a bug",
            _ => $"Closed: duplicate of {duplicateOf ?? "another bug"}",
        };
        if (!string.IsNullOrWhiteSpace(note)) text += $". {note.Trim()}";
        return SetStatus(bugId, You, false, _deviceName(), BugStatus.Closed, text,
            r => r with { CloseReason = reason, DuplicateOf = reason == BugCloseReason.Duplicate ? duplicateOf : null, PendingQuestion = null, FixingBy = null });
    }

    /// <summary>"It is fixed" on a bug waiting to be checked.</summary>
    public BugChange ConfirmFixed(string bugId) =>
        SetStatus(bugId, You, false, _deviceName(), BugStatus.Closed,
            Get(bugId)?.FixedIn is { } v ? $"You checked: fixed in {v}" : "You checked: fixed",
            r => r with { CloseReason = BugCloseReason.Fixed, PendingQuestion = null });

    /// <summary>Opens a closed bug, or one whose fix did not work, again, with why (and a new black box).</summary>
    public async Task<BugChange> ReopenAsync(string bugId, string why, IReadOnlyList<BugImageDraft> images, BlackBoxSnapshot? blackBox, CancellationToken ct = default)
    {
        Require(bugId);
        if (string.IsNullOrWhiteSpace(why)) throw new ArgumentException("Say why it is not fixed.");
        var attachments = await SaveAttachmentsAsync(images, blackBox, ct).ConfigureAwait(false);
        return SetStatus(bugId, You, false, _deviceName(), BugStatus.Open, $"Reopened: {why.Trim()}",
            r => r with { CloseReason = null, DuplicateOf = null, FixingBy = null, PendingQuestion = null }, attachments);
    }

    /// <summary>Takes a bug back from the agent working on it.</summary>
    public BugChange TakeBack(string bugId) =>
        SetStatus(bugId, You, false, _deviceName(), BugStatus.Open, "Taken back from " + (Get(bugId)?.FixingBy ?? "the agent"),
            r => r with { FixingBy = null, PendingQuestion = null });

    /// <summary>Marks the bug handed to an agent (the old Workspace hand-off; kept for bugs handed over before it was removed) until the agent claims it itself.</summary>
    public BugChange HandOver(string bugId, string to) =>
        SetStatus(bugId, You, false, _deviceName(), BugStatus.Fixing, $"Handed to {to}", r => r with { FixingBy = to });

    // ---- AI agents (MCP) ----

    /// <summary>An agent takes the bug. Refused when it is closed, waiting to be checked, or another agent has it.</summary>
    public BugChange Claim(string bugId, string agent, string? branch)
    {
        var report = Require(bugId);
        switch (report.Status)
        {
            case BugStatus.Closed:
                throw new InvalidOperationException($"#{report.Code} is closed. Only the user can reopen it.");
            case BugStatus.ToVerify:
                throw new InvalidOperationException($"#{report.Code} is fixed and waiting for the user to check it.");
            // The user's hand-off ("Handed to Claude Code · new session") waits for the agent it went to; another
            // agent's claim is refused.
            case BugStatus.Fixing when report.FixingBy is { } by && !SameAgent(by, agent) && !IsHandOff(bugId):
                throw new InvalidOperationException($"#{report.Code} is being fixed by {by}.");
        }
        return SetStatus(bugId, agent, true, ProjectOf(agent), BugStatus.Fixing, $"{agent} took it" + (branch is { Length: > 0 } ? $" (branch {branch})" : ""),
            r => r with { FixingBy = agent, Branch = string.IsNullOrWhiteSpace(branch) ? r.Branch : branch.Trim() });
    }

    public BugChange Note(string bugId, string agent, string text)
    {
        var before = Require(bugId);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Write the note.");
        var entry = AddEntry(bugId, BugEntryKind.AgentNote, agent, true, ProjectOf(agent), text.Trim(), Attachments.None);
        return new BugChange(bugId, before, before, entry);
    }

    /// <summary>The agent asks the user; the question waits on the bug until answered.</summary>
    public BugChange Ask(string bugId, string agent, string question)
    {
        var before = Require(bugId);
        if (before.Status == BugStatus.Closed) throw new InvalidOperationException($"#{before.Code} is closed.");
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Write the question.");
        var entry = AddEntry(bugId, BugEntryKind.Question, agent, true, ProjectOf(agent), question.Trim(), Attachments.None);
        var after = Update(bugId, r => r with { PendingQuestion = entry });
        return new BugChange(bugId, before, after, entry);
    }

    /// <summary>The agent fixed it: waiting for the user to check (never closed by an agent).</summary>
    public BugChange Resolve(string bugId, string agent, string resolution, string? fixedIn, string? branch)
    {
        var report = Require(bugId);
        if (report.Status == BugStatus.Closed) throw new InvalidOperationException($"#{report.Code} is closed.");
        if (string.IsNullOrWhiteSpace(resolution)) throw new ArgumentException("Say what the cause was and what was changed.");
        var text = resolution.Trim();
        return SetStatus(bugId, agent, true, ProjectOf(agent), BugStatus.ToVerify, text,
            r => r with
            {
                Resolution = text,
                FixedIn = string.IsNullOrWhiteSpace(fixedIn) ? r.FixedIn : fixedIn.Trim().TrimStart('v', 'V'),
                Branch = string.IsNullOrWhiteSpace(branch) ? r.Branch : branch.Trim(),
                FixingBy = null,
            });
    }

    /// <summary>The agent gives the bug back (it could not fix it).</summary>
    public BugChange Release(string bugId, string agent, string reason)
    {
        var report = Require(bugId);
        if (report.Status != BugStatus.Fixing) throw new InvalidOperationException($"#{report.Code} is not being fixed.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why.");
        return SetStatus(bugId, agent, true, ProjectOf(agent), BugStatus.Open, $"Gave it back: {reason.Trim()}", r => r with { FixingBy = null });
    }

    /// <summary>Takes back an agent's change (MCP Undo): the bug as it was, the entry gone.</summary>
    public bool Undo(BugChange change)
    {
        if (_reports.Get(change.BugId) is null) return false;
        _reports.Upsert(change.BugId, change.Before);
        if (change.EntryId is { } entry) _entries.Delete(entry);
        return true;
    }

    // ---- helpers ----

    internal static string TitleOf(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= MaxTitle ? line : line[..(MaxTitle - 1)].TrimEnd() + "…";
    }

    /// <summary>-1, 0 or 1 like "0.85.2" vs "0.86.0"; text that is not a version sorts as equal.</summary>
    public static int CompareVersions(string a, string b) =>
        Version.TryParse(Clean(a), out var x) && Version.TryParse(Clean(b), out var y) ? x.CompareTo(y) : 0;

    private static string Clean(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(['-', '+', ' ']);
        return cut > 0 ? v[..cut] : v;
    }

    private int NextNumber() => _reports.All().Select(r => r.Value.Number).DefaultIfEmpty(0).Max() + 1;

    private BugReport Require(string bugId) => _reports.Get(bugId) ?? throw new KeyNotFoundException($"No bug {bugId}.");

    private BugReport Update(string bugId, Func<BugReport, BugReport> change)
    {
        lock (_gate)
        {
            var after = change(Require(bugId));
            _reports.Upsert(bugId, after);
            return after;
        }
    }

    private BugChange SetStatus(string bugId, string author, bool byAgent, string device, BugStatus status, string text,
        Func<BugReport, BugReport> change, Attachments? attachments = null)
    {
        var before = Require(bugId);
        var now = _time.GetUtcNow();
        var after = Update(bugId, r => change(r) with { Status = status, StatusChangedAt = now });
        var entry = AddEntry(bugId, BugEntryKind.Status, author, byAgent, device, text, attachments ?? Attachments.None, status);
        return new BugChange(bugId, before, after, entry);
    }

    private string AddEntry(string bugId, BugEntryKind kind, string author, bool byAgent, string device, string text, Attachments attachments, BugStatus? status = null) =>
        _entries.Add(new BugEntry
        {
            BugId = bugId,
            Kind = kind,
            Author = author,
            ByAgent = byAgent,
            Device = device,
            Text = text,
            At = _time.GetUtcNow(),
            BlackBox = attachments.BlackBox,
            BlackBoxEvents = attachments.Events,
            BlackBoxErrors = attachments.Errors,
            Images = [.. attachments.Images],
            NewStatus = status,
        });

    private async Task<Attachments> SaveAttachmentsAsync(IReadOnlyList<BugImageDraft> images, BlackBoxSnapshot? blackBox, CancellationToken ct)
    {
        if (images.Count > MaxImages) throw new ArgumentException($"At most {MaxImages} images.");
        var saved = new List<BugAttachment>();
        foreach (var image in images)
        {
            if (image.Data.Length > MaxImageBytes) throw new ArgumentException($"{image.Name ?? "An image"} is larger than {MaxImageBytes / (1024 * 1024)} MB.");
            saved.Add(new BugAttachment(await _files.SaveAsync(image.Data, ct).ConfigureAwait(false), image.MimeType, image.Name));
        }
        BlobRef? box = blackBox is null ? null : await _files.SaveAsync(blackBox.ToBytes(), ct).ConfigureAwait(false);
        return new Attachments(saved, box, blackBox?.Events.Count ?? 0, blackBox?.ErrorCount ?? 0);
    }

    /// <summary>True when the bug went to Fixing by the user's hand-off, not by an agent's claim.</summary>
    private bool IsHandOff(string bugId) =>
        Entries(bugId).LastOrDefault(e => e.Value.Kind == BugEntryKind.Status) is { Value: { ByAgent: false, NewStatus: BugStatus.Fixing } };

    private static bool SameAgent(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Claude Code · helm" → "helm"; an agent without a project → its name.</summary>
    private static string ProjectOf(string agent)
    {
        var dot = agent.LastIndexOf('·');
        return dot >= 0 ? agent[(dot + 1)..].Trim() : agent;
    }

    private sealed record Attachments(IReadOnlyList<BugAttachment> Images, BlobRef? BlackBox, int Events, int Errors)
    {
        public static Attachments None { get; } = new([], null, 0, 0);
    }
}
