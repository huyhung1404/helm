using Helm.Core.Links;
using Helm.Core.Text;

namespace Helm.Modules.Wallet;

/// <summary>
/// People in the debt book as link targets, by the key of their name (every entry of theirs, settled or not). The kind
/// and the key are the ones the Tracker's debt book used, so notes linked to a person before the move still are.
/// </summary>
/// <param name="open">Shows the debt book with the given person open.</param>
public sealed class DebtLinkProvider : ILinkProvider
{
    private readonly DebtBook _book;
    private readonly Action<string> _open;

    public DebtLinkProvider(DebtBook book, Action<string> open)
    {
        _book = book;
        _open = open;
        _book.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Kind => LinkKinds.Person;

    public string KindName => "Person";

    public event EventHandler? Changed;

    public LinkTarget? Resolve(string id) => _book.People().FirstOrDefault(p => p.Key == id) is { } person ? Target(person) : null;

    public IEnumerable<LinkTarget> Search(string text, int max)
    {
        var terms = TextSearch.Terms(text);
        var people = _book.People();
        if (terms.Count == 0) return people.Take(max).Select(Target).ToList();
        return people
            .Select(p => (p, Score: TextSearch.Score(terms, p.Name)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => Target(x.p))
            .ToList();
    }

    public void Open(string id) => _open(id);

    private LinkTarget Target(DebtPerson person) =>
        new(new LinkRef(Kind, person.Key), person.Name, "Debt · " + (person.IsSettled ? "settled" : DebtFormat.Balance(person.Balance)));
}
