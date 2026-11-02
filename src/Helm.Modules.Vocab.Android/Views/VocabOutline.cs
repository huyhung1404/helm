using Avalonia;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>
/// One row of "How it's built" on the phone: a box of the word's tree, indented under its parent with an elbow from it.
/// The phone shows the tree as this outline (top to bottom) instead of the PC's wide tree, which would scroll sideways.
/// </summary>
public sealed class VocabOutlineRow
{
    private const double Step = 24;

    public VocabOutlineRow(VocabNodeViewModel node, int depth, VocabNodeViewModel root)
    {
        Node = node;
        Depth = depth;
        // Tapping the tapped part again goes back to the word itself (its part card closes).
        Pick = new RelayCommand(() => (node.IsSelected && !node.IsRoot ? root : node).SelectCommand.Execute(null));
        Meaning = string.Join(" · ", new[] { node.Caption, node.Node.Meaning }.Where(s => s.Length > 0));
    }

    public VocabNodeViewModel Node { get; }

    public int Depth { get; }

    /// <summary>Selects the box, or the word's own box when this one is selected already.</summary>
    public IRelayCommand Pick { get; }

    /// <summary>"jiù · CỨU · cứu, giúp thoát nạn": its reading and meaning, on one line beside the box.</summary>
    public string Meaning { get; }

    public bool HasMeaning => Meaning.Length > 0;

    public bool IsRoot => Node.IsRoot;

    /// <summary>A part (not the word itself) is joined to its parent by an elbow.</summary>
    public bool HasElbow => Depth > 0;

    /// <summary>The row's left padding: the word at the edge, each level 24 px further in.</summary>
    public Thickness Indent => new(6 + Math.Max(0, Depth - 1) * Step, 4, 10, 4);

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
