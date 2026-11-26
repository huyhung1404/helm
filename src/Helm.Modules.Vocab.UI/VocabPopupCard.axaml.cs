using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentIcons.Common;

namespace Helm.Modules.Vocab.Ui;

/// <summary>What only the window around the corner card can do (Windows: VocabPopupWindow).</summary>
public interface IVocabPopupHost
{
    /// <summary>Close (no answer), or the card was answered.</summary>
    void Close();

    /// <summary>"Open in Helm": the word with its whole tree on Vocabulary's Words tab.</summary>
    void OpenWord(string id);

    /// <summary>The pinyin box is used: the card takes the keyboard from the app in front.</summary>
    void TakeKeyboard();

    /// <summary>Done typing: the keyboard goes back to the app that had it.</summary>
    void ReturnKeyboard();

    /// <summary>The card was pressed (not a button or the box): it moves with the mouse.</summary>
    void Drag(PointerPressedEventArgs e);

    /// <summary>A double-click on the card: back to the corner.</summary>
    void BackToCorner();

    /// <summary>How tall the answer may grow while Full details is open (the screen's height less the card's other rows).</summary>
    double FullHeight { get; }
}

/// <summary>
/// The corner card (see the XAML); DataContext: <see cref="VocabCardViewModel"/>. It never asks for the keyboard
/// except while its pinyin box is used. At rest it is see-through (the settings' Card opacity), so the app underneath
/// stays readable; under the pointer, while typing and while Full details is open it is solid.
/// </summary>
public partial class VocabPopupCard : UserControl
{
    /// <summary>How tall the answer grows before it scrolls, unless Full details is open.</summary>
    public const double CompactHeight = 300;

    /// <summary>The lowest opacity at rest, whatever the setting says.</summary>
    public const double MinOpacity = VocabSettingsViewModel.MinCardOpacity / 100.0;

    private readonly VocabCardViewModel _card;
    private readonly IVocabPopupHost _host;
    private double _restOpacity = 1;
    private bool _typing, _full;

    public VocabPopupCard() : this(new VocabCardViewModel(null!, null!), new NoHost())
    {
    }

    /// <param name="restOpacity">How solid the card is at rest, 0-1 (the settings' Card opacity).</param>
    public VocabPopupCard(VocabCardViewModel card, IVocabPopupHost host, double restOpacity = 1)
    {
        _card = card;
        _host = host;
        DataContext = card;
        InitializeComponent();
        CloseButton.Click += (_, _) => _host.Close();
        OpenButton.Click += (_, _) =>
        {
            if (_card.WordId is { } id) _host.OpenWord(id);
            _host.Close();
        };
        FullButton.Click += (_, _) => ToggleFull();
        WordButton.Click += (_, _) =>
        {
            if (_card.IsTyping) StartTyping();
            else Reveal();
        };
        CheckButton.Click += (_, _) => Check();
        ShowMeaningButton.Click += (_, _) => Reveal();
        TypeBox.AddHandler(PointerPressedEvent, (_, _) => StartTyping(), RoutingStrategies.Tunnel, handledEventsToo: true);
        TypeBox.AddHandler(KeyDownEvent, OnTypeKey, RoutingStrategies.Tunnel);
        Card.PointerEntered += (_, _) => UpdateOpacity();
        Card.PointerExited += (_, _) => UpdateOpacity();
        Card.PointerPressed += OnCardPressed;
        card.PropertyChanged += OnCardChanged;
        DetachedFromVisualTree += (_, _) => card.PropertyChanged -= OnCardChanged;
        SetRestOpacity(restOpacity);
        Update();
    }

    /// <summary>Full details is open.</summary>
    public bool IsFull => _full;

    /// <summary>The pinyin box has the keyboard.</summary>
    public bool IsTyping => _typing;

    /// <summary>The card may go away by itself: untouched, unread and nothing typed.</summary>
    public bool CanGoAway => !Card.IsPointerOver && !_typing && !_card.IsRevealed && _card.Typed.Length == 0;

    /// <summary>How solid the card is now (see-through at rest).</summary>
    public double CardOpacity => Card.Opacity;

    /// <summary>How solid the card is at rest (the setting, which may change while it is shown).</summary>
    public void SetRestOpacity(double opacity)
    {
        _restOpacity = Math.Clamp(opacity, MinOpacity, 1);
        UpdateOpacity();
    }

    /// <summary>Opens Full details, or closes it.</summary>
    public void ToggleFull()
    {
        _full = !_full;
        if (_full) DetailsList.ItemsSource = VocabPopupSection.Build(_card);
        Scroller.MaxHeight = _full ? Math.Max(CompactHeight, _host.FullHeight) : CompactHeight;
        UpdateOpacity();
        Update();
        Scroller.Offset = default;
    }

    /// <summary>The pinyin box was used and is done with (Escape, Check, Show meaning): the keyboard goes back.</summary>
    public void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        UpdateOpacity();
        _host.ReturnKeyboard();
    }

    private void UpdateOpacity() => Card.Opacity = Card.IsPointerOver || _typing || _full ? 1 : _restOpacity;

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabCardViewModel.IsRevealed) or nameof(VocabCardViewModel.IsTyping) or nameof(VocabCardViewModel.AsksPinyin)
            or nameof(VocabCardViewModel.Text))
            Update();
    }

    /// <summary>What shows: the hint or the pinyin box, Full details, the Full details button's state.</summary>
    private void Update()
    {
        var typing = _card.IsTyping;
        TapHint.IsVisible = !typing && !_card.IsRevealed;
        TypePanel.IsVisible = typing;
        WordText.FontSize = _card.AsksPinyin ? 40 : 36;
        DetailsPanel.IsVisible = _full;
        FullIcon.Symbol = _full ? Symbol.ArrowMinimize : Symbol.ArrowMaximize;
        var name = _full ? "Less" : "Full details";
        ToolTip.SetTip(FullButton, name);
        AutomationProperties.SetName(FullButton, name);
        FullButton.Classes.Set("selected", _full);
        if (!typing) StopTyping();
    }

    /// <summary>Dragging the card (not its buttons or box) moves it; a double-click puts it back in the corner.</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) _host.BackToCorner();
        else _host.Drag(e);
    }

    private void OnTypeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Check();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StopTyping();
        }
    }

    private void Check()
    {
        if (_card.Typed.Trim().Length == 0)
        {
            StartTyping();
            return;
        }
        StopTyping();
        _card.CheckCommand.Execute(null);
    }

    private void Reveal()
    {
        StopTyping();
        if (!_card.IsRevealed) _card.RevealCommand.Execute(null);
    }

    /// <summary>The box is used: the card takes the keyboard from the app in front, which gets it back afterwards.</summary>
    private void StartTyping()
    {
        if (_typing) return;
        _typing = true;
        UpdateOpacity();
        _host.TakeKeyboard();
        Dispatcher.UIThread.Post(() => TypeBox.Focus(), DispatcherPriority.Input);
    }

    private sealed class NoHost : IVocabPopupHost
    {
        public void Close() { }
        public void OpenWord(string id) { }
        public void TakeKeyboard() { }
        public void ReturnKeyboard() { }
        public void Drag(PointerPressedEventArgs e) { }
        public void BackToCorner() { }
        public double FullHeight => CompactHeight;
    }
}

/// <summary>One section of the corner card's Full details, with how it is drawn.</summary>
public sealed record VocabPopupSection(string Title, IReadOnlyList<VocabDetailItem> Items, bool IsTree)
{
    /// <summary>"To remember it": in its box, first.</summary>
    public bool IsMemo => Title == "To remember it";

    /// <summary>"Easy to mix up with": the title in the warning colour.</summary>
    public bool IsWarn => Title == "Easy to mix up with";

    // The phone's section names, as the PC card says them.
    private static readonly Dictionary<string, string> PcTitles = new()
    {
        ["Easy to mix up"] = "Easy to mix up with",
        ["Synonyms"] = "Similar",
        ["Antonyms"] = "Opposite",
    };

    /// <summary>
    /// Everything else known about the word, under the parts: "To remember it" first, how the parts make the meaning,
    /// then the sections under the PC card's names; the pinyin hook is already above. Plain words ("Goes with",
    /// "Similar") on one line: 补救措施 · 无法补救.
    /// </summary>
    public static List<VocabPopupSection> Build(VocabCardViewModel card)
    {
        var sections = card.Details().Where(s => s.Title != "Remembering the pinyin")
            .Select(s => new VocabPopupSection(PcTitles.TryGetValue(s.Title, out var title) ? title : s.Title, s.Items, s.IsTree))
            .Select(s => s is { IsTree: false, Items.Count: > 1 } && s.Items.All(i => !i.HasNote)
                ? s with { Items = [new VocabDetailItem(string.Join(" · ", s.Items.Select(i => i.Text)))] } : s)
            .ToList();
        if (card.Explanation.Length > 0) sections.Insert(0, new VocabPopupSection("How the parts make it", [new VocabDetailItem(card.Explanation)], false));
        if (sections.FindIndex(s => s.IsMemo) is var memo and > 0)
        {
            var section = sections[memo];
            sections.RemoveAt(memo);
            sections.Insert(0, section);
        }
        return sections;
    }
}
