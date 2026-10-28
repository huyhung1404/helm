using System.Text.Json;

namespace Helm.Modules.Workspace;

/// <summary>
/// The PC's running record of one session's timeline, in pages of <see cref="WorkspaceSync.TimelinePageSize"/> steps.
/// Each read of the transcript sees only its end; merging it by step id keeps the steps seen before, updates the ones
/// that grew (an answer's next block, a tool's result) and adds the new ones after them, so a step keeps its page.
/// The oldest pages are dropped beyond <see cref="WorkspaceSync.TimelinePages"/>. A read that shares no step with what
/// is kept (the transcript was replaced, or ran far ahead) starts a new <see cref="Epoch"/>.
/// </summary>
public sealed class TimelineLedger
{
    private readonly List<TranscriptItem> _items = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly HashSet<int> _dirty = [];

    public string Epoch { get; private set; } = NewEpoch();
    public int FirstPage { get; private set; }
    public int LastPage => FirstPage + Math.Max(0, _items.Count - 1) / WorkspaceSync.TimelinePageSize;
    public int Count => _items.Count;

    /// <summary>
    /// Takes in the steps of a read, oldest first. False (and nothing kept) when a step has no id: such a timeline
    /// cannot be paged and goes in the session record as before.
    /// </summary>
    public bool Merge(IReadOnlyList<TranscriptItem> read)
    {
        if (read.Any(i => i.Id is null))
        {
            if (_items.Count > 0) Reset();
            return false;
        }
        // The newest step of the read already kept: what comes after it is new. Unknown steps before it are the read's
        // cut-off start (an answer whose first block lay before the tail), not new.
        var last = -1;
        for (var i = read.Count - 1; i >= 0 && _items.Count > 0; i--)
        {
            if (_index.ContainsKey(read[i].Id!)) { last = i; break; }
        }
        if (_items.Count > 0 && last < 0 && read.Count > 0) Reset();
        for (var i = 0; i < read.Count; i++)
        {
            var item = read[i];
            if (_index.TryGetValue(item.Id!, out var at))
            {
                if (_items[at] == item) continue;
                _items[at] = item;
                _dirty.Add(PageOf(at));
            }
            else if (i > last)
            {
                _index[item.Id!] = _items.Count;
                _dirty.Add(PageOf(_items.Count));
                _items.Add(item);
            }
        }
        while (_items.Count > WorkspaceSync.MaxTimelineItems)
        {
            _items.RemoveRange(0, WorkspaceSync.TimelinePageSize);
            FirstPage++;
            Reindex();
        }
        return true;
    }

    /// <summary>The pages whose steps changed since the last call (still kept ones only), and forgets them.</summary>
    public IReadOnlyList<int> TakeChangedPages()
    {
        var pages = _dirty.Where(p => p >= FirstPage && p <= LastPage).Order().ToList();
        _dirty.Clear();
        return pages;
    }

    private int PageOf(int index) => FirstPage + index / WorkspaceSync.TimelinePageSize;

    /// <summary>The steps of <paramref name="page"/> (<see cref="FirstPage"/> to <see cref="LastPage"/>).</summary>
    public IReadOnlyList<TranscriptItem> Page(int page)
    {
        var start = (page - FirstPage) * WorkspaceSync.TimelinePageSize;
        if (page < FirstPage || start >= _items.Count) return [];
        return _items.GetRange(start, Math.Min(WorkspaceSync.TimelinePageSize, _items.Count - start));
    }

    /// <summary>
    /// <paramref name="items"/> cut to fit a page record: the longest texts and outputs are shortened (with "…") until
    /// the page weighs at most <see cref="WorkspaceSync.MaxPageBytes"/>. The same steps always fit the same way.
    /// </summary>
    public static IReadOnlyList<TranscriptItem> Fit(IReadOnlyList<TranscriptItem> items)
    {
        var kept = items.ToList();
        var sizes = kept.Select(Size).ToList();
        var total = sizes.Sum();
        while (total > WorkspaceSync.MaxPageBytes)
        {
            var big = sizes.IndexOf(sizes.Max());
            var item = kept[big];
            var text = item.Text.Length >= (item.Output?.Length ?? 0);
            var length = text ? item.Text.Length : item.Output!.Length;
            if (length <= 200) break;
            kept[big] = text
                ? item with { Text = WorkspaceSync.Cut(item.Text, length / 2) ?? "" }
                : item with { Output = WorkspaceSync.Cut(item.Output, length / 2) };
            total -= sizes[big];
            sizes[big] = Size(kept[big]);
            total += sizes[big];
        }
        return kept;
    }

    private static int Size(TranscriptItem item) => JsonSerializer.SerializeToUtf8Bytes(item).Length;

    private void Reset()
    {
        _items.Clear();
        _index.Clear();
        _dirty.Clear();
        FirstPage = 0;
        Epoch = NewEpoch();
    }

    private void Reindex()
    {
        _index.Clear();
        for (var i = 0; i < _items.Count; i++) _index[_items[i].Id!] = i;
    }

    private static string NewEpoch() => Guid.NewGuid().ToString("N")[..12];
}
