using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// One row of "How it's built" on a narrow screen: a box of the word's tree, indented under its parent. A phone shows
/// the tree as this outline, top to bottom, instead of the wide tree (<see cref="VocabTreeView"/>), which would scroll
/// sideways.
/// </summary>
public sealed class VocabOutlineRow : ObservableObject
{
    private const double Step = 22;

    public VocabOutlineRow(VocabNodeViewModel node, int depth, VocabNodeViewModel root)
    {
        Node = node;
        Depth = depth;
        // Tapping the tapped part again goes back to the word itself (its part card closes).
        Pick = new RelayCommand(() => (node.IsSelected && !node.IsRoot ? root : node).SelectCommand.Execute(null));
        Meaning = node.Node.Meaning.Length > 0 ? node.Node.Meaning : node.Caption;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VocabNodeViewModel.IsSelected)) OnPropertyChanged(nameof(IsPicked));
        };
    }

    public VocabNodeViewModel Node { get; }

    public int Depth { get; }

    /// <summary>Selects the box, or the word's own box when this one is selected already.</summary>
    public IRelayCommand Pick { get; }

    /// <summary>"cứu, giúp thoát nạn": its meaning on one line beside the box (its reading when it has none).</summary>
    public string Meaning { get; }

    public bool HasMeaning => Meaning.Length > 0;

    public bool IsRoot => Node.IsRoot;

    /// <summary>The tapped part; the word's own box never is (tapping it closes the part's card).</summary>
    public bool IsPicked => Node.IsSelected && !Node.IsRoot;

    /// <summary>A part (not the word) hangs from its parent by a short line.</summary>
    public bool HasElbow => Depth > 0;

    /// <summary>The row's padding: the word at the edge, each level further in.</summary>
    public Thickness Indent => new(6 + Math.Max(0, Depth - 1) * Step, 3, 10, 3);

    /// <summary>The tree, depth first: the word, then each part followed by its own parts.</summary>
    public static IReadOnlyList<VocabOutlineRow> Flatten(VocabNodeViewModel? root)
    {
        if (root is null) return [];
        var rows = new List<VocabOutlineRow>();
        void Walk(VocabNodeViewModel node, int depth)
        {
            rows.Add(new VocabOutlineRow(node, depth, root));
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
        Walk(root, 0);
        return rows;
    }
}
