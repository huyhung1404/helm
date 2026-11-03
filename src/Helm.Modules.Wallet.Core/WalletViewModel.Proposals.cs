using Microsoft.Extensions.Logging;

namespace Helm.Modules.Wallet;

// "To categorize": AI's category for a payment the history cannot file, shown first, as on the phone's "What was it?"
// notification. A new category it proposes is a "+ Name" chip, made only when clicked.
public sealed partial class WalletViewModel
{
    /// <summary>The waiting transactions AI is asked about each time the list opens (the first ones; the rest wait).</summary>
    internal const int ProposeAtOnce = 5;

    private static readonly TimeSpan ProposeFor = TimeSpan.FromMinutes(2);

    private readonly WalletCategoryAi? _categoryAi;

    /// <summary>AI's answer per transaction this session (null: it could not tell), so reopening the list asks no more.</summary>
    private readonly Dictionary<string, CategoryProposal?> _proposals = new(StringComparer.Ordinal);

    private readonly HashSet<string> _proposing = new(StringComparer.Ordinal);

    /// <summary>The row's one-click categories with AI's pick first; a new name it proposed goes on the row's "+" chip.</summary>
    private IReadOnlyList<CategoryInfo> WithProposal(TransactionRow row, IReadOnlyList<CategoryInfo> suggestions)
    {
        var proposal = _proposals.GetValueOrDefault(row.Id);
        row.SetProposedCategory(proposal?.NewName);
        if (proposal is not { CategoryId: { } id } || _store.Category(id) is not { Hidden: false } picked) return suggestions;
        return [picked, .. suggestions.Where(s => s.Id != id).Take(SuggestionCount - 1)];
    }

    /// <summary>
    /// Asks AI (<see cref="WalletCategoryAi"/>: the history first, then a PC agent, the API only if the user chose so)
    /// about the first waiting transactions it has not been asked about, one after another; each answer shows as it comes.
    /// </summary>
    private void ProposeCategories()
    {
        if (_categoryAi is not { } ai) return;
        var ids = ToCategorize.Select(r => r.Id).Where(id => !_proposals.ContainsKey(id) && !_proposing.Contains(id)).Take(ProposeAtOnce).ToList();
        if (ids.Count == 0) return;
        _proposing.UnionWith(ids);
        _ = Task.Run(async () =>
        {
            foreach (var id in ids)
            {
                CategoryProposal? proposal = null;
                try
                {
                    using var limit = new CancellationTokenSource(ProposeFor);
                    proposal = await ai.ProposeAsync(id, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Too slow: the history's suggestions stay.
                }
                catch (Exception ex)
                {
                    // In the background: the list works without it.
                    _logger.LogDebug(ex, "AI could not propose a category");
                }
                _ui.Post(() =>
                {
                    _proposing.Remove(id);
                    _proposals[id] = proposal;
                    if (proposal is not null) ScheduleRefresh();
                });
            }
        });
    }

    /// <summary>A "+ Name" chip: the category AI proposed is made now, and the transaction goes in it.</summary>
    internal void AcceptProposedCategory(TransactionRow row)
    {
        if (row.ProposedCategory is not { Length: > 0 } name) return;
        if (CreateCategoryFor(row, name)) _proposals.Remove(row.Id);
    }
}
