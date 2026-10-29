using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>
/// One box of the tree on screen. The flags tell the page which connector halves to draw above a box: a line from the
/// left for every child but the first, to the right for every child but the last, so siblings join under their parent.
/// </summary>
public sealed partial class VocabNodeViewModel : ObservableObject
{
    private readonly Action<VocabNodeViewModel> _select;

    [ObservableProperty] private bool _isSelected;

    public VocabNodeViewModel(VocabTreeNode node, bool showTraditional, Action<VocabNodeViewModel> select, bool isRoot = true, bool isFirst = true, bool isLast = true)
    {
        _select = select;
        Node = node;
        IsRoot = isRoot;
        IsFirst = isFirst;
        IsLast = isLast;
        Form = showTraditional && node.Traditional.Length > 0 ? $"{node.Form} ({node.Traditional})" : node.Form;
        // "jiù · CỨU" as in Chinese dictionaries; English parts show their IPA or nothing.
        Caption = string.Join(" · ", new[] { node.Phonetic, node.HanViet.ToUpperInvariant() }.Where(s => s.Length > 0));
        Children = node.Children.Select((c, i) => new VocabNodeViewModel(c, showTraditional, select, false, i == 0, i == node.Children.Count - 1)).ToList();
    }

    public VocabTreeNode Node { get; }

    public string Form { get; }

    public string Caption { get; }

    public bool HasCaption => Caption.Length > 0;

    /// <summary>Short meaning under the box (the card below has it all).</summary>
    public string ShortMeaning => Node.Meaning.Length <= 18 ? Node.Meaning : Node.Meaning[..17].TrimEnd() + "…";

    public bool HasShortMeaning => Node.Meaning.Length > 0;

    public string RoleText => Node.Role switch
    {
        VocabPartRole.Meaning => "meaning",
        VocabPartRole.Sound => "sound",
        _ => "",
    };

    public bool HasRole => Node.Role != VocabPartRole.Plain;

    public bool IsSound => Node.Role == VocabPartRole.Sound;

    public IReadOnlyList<VocabNodeViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsRoot { get; }

    public bool IsFirst { get; }

    public bool IsLast { get; }

    /// <summary>A line comes up into this box from its parent.</summary>
    public bool HasParent => !IsRoot;

    public bool LineFromLeft => !IsRoot && !IsFirst;

    public bool LineToRight => !IsRoot && !IsLast;

    public IEnumerable<VocabNodeViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    [RelayCommand]
    private void Select() => _select(this);
}
