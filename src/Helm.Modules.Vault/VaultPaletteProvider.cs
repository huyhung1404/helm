using Helm.Core.Palette;
using Helm.Core.Services;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vault;

/// <summary>
/// Vault in the command palette, only while the vault is unlocked: items whose title or tags match (never their
/// fields or notes), and "Lock the vault". Choosing an item opens Vault on it; nothing is copied. While the vault is
/// locked the palette knows nothing about it, so no title can be seen without the password.
/// </summary>
internal sealed class VaultPaletteProvider(VaultSession session, VaultStore store, VaultAppViewModel app, IServiceProvider services) : IPaletteProvider
{
    private const int MaxResults = 8;

    public string? ModuleId => VaultModule.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (session.State != VaultState.Unlocked || query.IsEmpty) return [];
        var results = new List<PaletteItem>();
        var lockScore = Math.Max(query.Score("Lock the vault"), query.Score("Khóa két"));
        if (lockScore > 0) results.Add(new PaletteItem("Lock the vault", "Vault", PaletteKind.Action, lockScore, () => session.Lock("palette")));
        foreach (var entry in store.Items())
        {
            var score = query.Score(entry.Item.Title, string.Join(' ', entry.Item.Tags));
            if (score <= 0) continue;
            var uid = entry.Uid;
            results.Add(new PaletteItem(entry.Item.Title, $"Vault · {Kind(entry.Item.Kind)}", PaletteKind.VaultItem, score, () => Open(uid)));
        }
        return results.OrderByDescending(r => r.Score).Take(MaxResults);
    }

    private void Open(string uid)
    {
        services.GetRequiredService<IShellNavigation>().ShowPage(typeof(VaultContentPage));
        app.Items.Reveal(uid);
    }

    private static string Kind(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => "Login",
        VaultItemKind.Note => "Secure note",
        VaultItemKind.Card => "Card",
        VaultItemKind.Identity => "Identity",
        VaultItemKind.Document => "Document",
        VaultItemKind.Token => "Token",
        _ => kind.ToString(),
    };
}
