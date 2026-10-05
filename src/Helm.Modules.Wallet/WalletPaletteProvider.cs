using Helm.Core.Palette;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet in the command palette: the people in the debt book with an open balance whose names match. Choosing one
/// opens the debt book on them. The shell navigation is resolved when a result runs (it depends on the module list,
/// which the palette module is part of).
/// </summary>
internal sealed class WalletPaletteProvider(DebtBook book, WalletViewModel viewModel, IServiceProvider services) : IPaletteProvider
{
    private const int MaxResults = 6;

    public string? ModuleId => WalletIds.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty) return [];
        return book.Open().Where(p => !p.IsSettled)
            .Select(p => (Person: p, Score: query.Score(p.Name)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(MaxResults)
            .Select(x => new PaletteItem(x.Person.Name, $"Debt · {DebtFormat.Balance(x.Person.Balance)}", PaletteKind.Debt, x.Score, () => Show(x.Person.Key)))
            .ToList();
    }

    private void Show(string key)
    {
        services.GetRequiredService<IShellNavigation>().ShowPage(typeof(WalletContentPage));
        viewModel.ShowPerson(key);
    }
}
