using System.Security.Cryptography;
using System.Text;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Core.Links;

/// <summary>Something a link can point at: a note, a task, a person in the debt book… <see cref="Kind"/> says which tool.</summary>
public readonly record struct LinkRef(string Kind, string Id)
{
    public override string ToString() => Kind + ":" + Id;

    public static LinkRef Parse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) throw new FormatException($"Not a link reference: {text}");
        return new LinkRef(text[..colon], text[(colon + 1)..]);
    }
}

/// <summary>The kinds the tools register. A kind never changes once released: links store it.</summary>
public static class LinkKinds
{
    public const string Note = "note";

    /// <summary>A task; for a task that repeats, its series (every day's task is the same target).</summary>
    public const string Task = "task";

    /// <summary>A person in the debt book, by <c>DebtLedger.Key</c> of their name (all their entries).</summary>
    public const string Person = "person";

    /// <summary>A mission (all its steps).</summary>
    public const string Mission = "mission";
}

/// <summary>
/// One link between two things (synced record in <c>links.items</c>). <see cref="A"/> and <see cref="B"/> are stored
/// in order, and the record id comes from the pair, so linking the same two things on two devices writes one record.
/// </summary>
public sealed record HelmLink
{
    public string A { get; init; } = "";

    public string B { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>How a link target looks in a list: its title and a line saying what it is.</summary>
public sealed record LinkTarget(LinkRef Ref, string Title, string Subtitle);

/// <summary>
/// What a tool offers to links: find its things by text, describe one, and open it. Registered once per kind with
/// <c>services.AddSingleton&lt;ILinkProvider, …&gt;()</c>. Called on the UI thread.
/// </summary>
public interface ILinkProvider
{
    string Kind { get; }

    /// <summary>"Note", "Task", "Debt": what the picker calls these.</summary>
    string KindName { get; }

    /// <returns>Null when the thing is gone (deleted, in the trash): its links are then hidden, not removed.</returns>
    LinkTarget? Resolve(string id);

    /// <summary>The best matches for <paramref name="text"/> (the most recent ones when it is empty).</summary>
    IEnumerable<LinkTarget> Search(string text, int max);

    /// <summary>Shows it in its tool.</summary>
    void Open(string id);

    /// <summary>Makes a new one titled <paramref name="title"/> and returns its id; null when this kind cannot be created from a link.</summary>
    string? Create(string title) => null;

    /// <summary>Raised when things of this kind changed (e.g. a title), so the lists showing them refresh.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Every link, plus the providers that describe their ends. The tools' view models ask it for the links of the thing
/// they show, and refresh when <see cref="Changed"/> fires (links added or removed on any device, or targets edited).
/// </summary>
public sealed class LinkHub
{
    public const string Collection = "links.items";
    private readonly ISyncedCollection<HelmLink> _links;
    private readonly Lazy<IReadOnlyDictionary<string, ILinkProvider>> _providers;
    private readonly TimeProvider _time;

    /// <param name="providers">Resolved on first use: the providers depend on the tools, which depend on this hub.</param>
    public LinkHub(ISyncedCollection<HelmLink> links, Func<IEnumerable<ILinkProvider>> providers, TimeProvider? time = null)
    {
        _links = links;
        _time = time ?? TimeProvider.System;
        _providers = new Lazy<IReadOnlyDictionary<string, ILinkProvider>>(() =>
        {
            var byKind = providers().GroupBy(p => p.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var provider in byKind.Values) provider.Changed += (_, _) => RaiseChanged();
            return byKind;
        });
        _links.Changed += (_, _) => RaiseChanged();
    }

    /// <summary>Raised on any thread: marshal to the UI before touching it.</summary>
    public event EventHandler? Changed;

    public ILinkProvider? Provider(string kind) => _providers.Value.GetValueOrDefault(kind);

    /// <summary>What <paramref name="item"/> is linked to, in the order the links were made.</summary>
    public IReadOnlyList<LinkRef> LinksOf(LinkRef item)
    {
        var self = item.ToString();
        var found = new List<(LinkRef Ref, DateTimeOffset At)>();
        foreach (var (_, link, _) in _links.All())
        {
            if (link.A == self) found.Add((LinkRef.Parse(link.B), link.CreatedAt));
            else if (link.B == self) found.Add((LinkRef.Parse(link.A), link.CreatedAt));
        }
        return found.OrderBy(f => f.At).Select(f => f.Ref).ToList();
    }

    /// <summary>The links of <paramref name="item"/> whose other end still exists, described by its tool.</summary>
    public IReadOnlyList<LinkTarget> TargetsOf(LinkRef item)
    {
        var targets = new List<LinkTarget>();
        foreach (var other in LinksOf(item))
        {
            if (Provider(other.Kind)?.Resolve(other.Id) is { } target) targets.Add(target);
        }
        return targets;
    }

    public bool IsLinked(LinkRef a, LinkRef b) => _links.Get(IdOf(a, b)) is not null;

    /// <returns>False when they were linked already.</returns>
    public bool Link(LinkRef a, LinkRef b)
    {
        if (a == b) throw new ArgumentException("A thing cannot be linked to itself.");
        if (IsLinked(a, b)) return false;
        var (first, second) = Order(a, b);
        _links.Upsert(IdOf(a, b), new HelmLink { A = first.ToString(), B = second.ToString(), CreatedAt = _time.GetUtcNow() });
        return true;
    }

    /// <returns>False when they were not linked.</returns>
    public bool Unlink(LinkRef a, LinkRef b) => _links.Delete(IdOf(a, b));

    /// <summary>The record id of the pair: the same on every device, whichever end is given first.</summary>
    public static string IdOf(LinkRef a, LinkRef b)
    {
        var (first, second) = Order(a, b);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(first + "\n" + second));
        return "l" + Convert.ToHexString(hash, 0, 20).ToLowerInvariant();
    }

    private static (LinkRef, LinkRef) Order(LinkRef a, LinkRef b) =>
        string.CompareOrdinal(a.ToString(), b.ToString()) <= 0 ? (a, b) : (b, a);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { /* a view model's refresh must not break a sync or another tool */ }
    }
}

public static class LinkServices
{
    /// <summary>
    /// The links between tools (collection <c>links.items</c>). Each tool that shows links calls this; registering twice is
    /// harmless. Tools add their <see cref="ILinkProvider"/> separately.
    /// </summary>
    public static IServiceCollection AddHelmLinks(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(LinkHub))) return services;
        services.AddSyncedCollection(new SyncedCollectionOptions<HelmLink> { Name = LinkHub.Collection });
        services.AddSingleton(sp => new LinkHub(sp.GetRequiredService<ISyncedCollection<HelmLink>>(), sp.GetServices<ILinkProvider>));
        return services;
    }
}
