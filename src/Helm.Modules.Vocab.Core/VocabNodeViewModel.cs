using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>
/// One box of the tree on screen. The flags tell the page which connector halves to draw above a box: a line from the
/// left for every child but the first, to the right for every child but the last, so siblings join under their parent.
/// A box that is a word by itself (a Chinese character, an English word inside a longer one) can become one of the
/// user's words (right-click on the PC, hold on the phone).
/// </summary>
public sealed partial class VocabNodeViewModel : ObservableObject
{
    private readonly Action<VocabNodeViewModel> _select;
    private readonly Action<VocabNodeViewModel>? _addAsWord;

    [ObservableProperty] private bool _isSelected;

    public VocabNodeViewModel(VocabTreeNode node, bool showTraditional, Action<VocabNodeViewModel> select, bool isRoot = true, bool isFirst = true, bool isLast = true,
        Action<VocabNodeViewModel>? addAsWord = null, Func<string, bool>? isWord = null)
    {
        _select = select;
        _addAsWord = addAsWord;
        Node = node;
        IsRoot = isRoot;
        IsFirst = isFirst;
        IsLast = isLast;
        Form = showTraditional && node.Traditional.Length > 0 ? $"{node.Form} ({node.Traditional})" : node.Form;
        // "jiù · CỨU" as in Chinese dictionaries; English parts show their IPA or nothing.
        Caption = string.Join(" · ", new[] { node.Phonetic, node.HanViet.ToUpperInvariant() }.Where(s => s.Length > 0));
        Children = node.Children.Select((c, i) => new VocabNodeViewModel(c, showTraditional, select, false, i == 0, i == node.Children.Count - 1, addAsWord, isWord)).ToList();
        CanAddAsWord = !isRoot && addAsWord is not null && IsWordByItself(node);
        IsOneOfYourWords = CanAddAsWord && isWord?.Invoke(node.Form) == true;
    }

    /// <summary>This box can become one of the user's words.</summary>
    public bool CanAddAsWord { get; }

    /// <summary>The user has this word already: the action opens it.</summary>
    public bool IsOneOfYourWords { get; }

    /// <summary>"Add 挽 as a word", or "Open 挽" when the user has it.</summary>
    public string AddAsWordText => IsOneOfYourWords ? $"Open “{Node.Form}”" : $"Add “{Node.Form}” as a word";

    /// <summary>A Chinese character or component written as one character, or an English word (not an affix or a Latin root).</summary>
    internal static bool IsWordByItself(VocabTreeNode node) => node.Kind switch
    {
        VocabPartKind.Character => true,
        VocabPartKind.Component => VocabText.Length(node.Form) == 1 && node.Form.Any(VocabText.IsHan),
        VocabPartKind.Word => node.Form.Trim('-').Length > 0,
        _ => false,
    };

    [RelayCommand]
    private void AddAsWord() => _addAsWord?.Invoke(this);

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
