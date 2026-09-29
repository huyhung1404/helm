using Helm.App.Views.Pages;
using Helm.Core.Modules;
using Helm.Core.Palette;
using Helm.Core.Sync;

namespace Helm.App.Services;

/// <summary>
/// Helm itself in the command palette: its pages (Home, General, every tool and its settings), every settings card
/// (the same index as the title-bar search) and "Sync now". Choosing one shows Helm's window on it.
/// </summary>
internal sealed class ShellPaletteProvider(SearchService search, IShellNavigator navigator, IModuleHost modules, ISyncService sync)
    : IPaletteProvider
{
    private const int MaxResults = 8;

    public string? ModuleId => null;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty) return [];
        var results = new List<PaletteItem>();
        void Add(string title, string subtitle, PaletteKind kind, Action run, string? detail = null)
        {
            var score = query.Score(title, detail);
            if (score > 0) results.Add(new PaletteItem(title, subtitle, kind, score, run));
        }

        Add("Home", "Helm", PaletteKind.Page, () => navigator.Navigate(typeof(HomePage)));
        Add("General", "Helm settings: theme, startup, updates, sync", PaletteKind.Page, () => navigator.Navigate(typeof(GeneralPage)), "settings");
        Add("Sync now", "Send and get changes from your other devices", PaletteKind.Action, () => _ = sync.SyncNowAsync(), "dong bo");

        foreach (var module in modules.Modules)
        {
            if (module is IModuleContent content)
            {
                var pageType = content.ContentPageType;
                Add(module.DisplayName, $"Open {module.DisplayName}", PaletteKind.Page, () => navigator.Navigate(pageType), module.Description);
                var settingsType = module.SettingsPageType;
                Add($"{module.DisplayName} settings", module.Group.DisplayName(), PaletteKind.Setting, () => navigator.Navigate(settingsType));
            }
            else
            {
                var settingsType = module.SettingsPageType;
                Add(module.DisplayName, module.Group.DisplayName(), PaletteKind.Page, () => navigator.Navigate(settingsType), module.Description);
            }
        }

        // Settings cards: "Activation shortcut · Quick Capture".
        foreach (var card in search.All().Where(r => r.Target is not null))
        {
            var target = card.Target;
            var pageType = card.PageType;
            Add(card.Title, $"Setting in {card.Location}", PaletteKind.Setting, () => navigator.Navigate(pageType, target), card.Location);
        }

        return results.OrderByDescending(r => r.Score).Take(MaxResults);
    }
}
