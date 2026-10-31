using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Settings;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab;

/// <summary>A choice of how often the PC pop-up comes (seconds between, picked at random).</summary>
public sealed record VocabPopupFrequency(string Name, int MinSeconds, int MaxSeconds);

/// <summary>
/// Vocabulary's options (the settings page, and the card mode on the Learn tab). Each change is saved at once and
/// reported through <see cref="Changed"/> so the page can follow it. UI thread only.
/// </summary>
public sealed partial class VocabSettingsViewModel : ObservableObject
{
    public static IReadOnlyList<VocabPopupFrequency> PopupFrequencies { get; } =
    [
        new("Often (every 10–30 minutes)", 10 * 60, 30 * 60),
        new("Now and then (every 20–60 minutes)", 20 * 60, 60 * 60),
        new("Rarely (every 1–2 hours)", 60 * 60, 120 * 60),
        new("Seldom (every 2–4 hours)", 120 * 60, 240 * 60),
    ];

    /// <summary>The shortest custom gap: a card stays up to 90 seconds unanswered, so less than this only stacks up.</summary>
    public const int MinPopupSeconds = 5;

    public const int MaxPopupSeconds = 24 * 60 * 60;

    /// <summary>Index of "Custom" in the choices: after the fixed ones.</summary>
    public static int CustomFrequencyIndex => PopupFrequencies.Count;

    /// <summary>The card modes, in <see cref="VocabCardMode"/> order.</summary>
    public static IReadOnlyList<string> CardModeNames { get; } = ["Word → meaning", "Meaning → word", "Listen → word", "Fill the gap", "Mixed", "Type the word"];

    private readonly ISettingsStore<VocabSettings> _settings;
    private readonly bool _loading;

    [ObservableProperty] private bool _showTraditional;
    [ObservableProperty] private bool _showYouGlish;
    [ObservableProperty] private bool _bubbleEnabled;
    [ObservableProperty] private bool _autoSay;
    [ObservableProperty] private bool _typePinyin;
    [ObservableProperty] private bool _autoAnalyze;
    [ObservableProperty] private int _cardModeIndex;
    [ObservableProperty] private int _itemsShownIndex;
    [ObservableProperty] private bool _popupEnabled;
    [ObservableProperty] private int _popupFrequencyIndex;
    [ObservableProperty] private string _customFromText = "";
    [ObservableProperty] private string _customToText = "";

    /// <summary>What is wrong with the typed range; empty when it is saved.</summary>
    [ObservableProperty] private string _customError = "";

    public VocabSettingsViewModel(ISettingsStore<VocabSettings> settings)
    {
        _settings = settings;
        _loading = true;
        var s = settings.Current;
        ShowTraditional = s.ShowTraditional;
        ShowYouGlish = s.ShowYouGlish;
        BubbleEnabled = s.BubbleEnabled;
        AutoSay = s.AutoSay;
        TypePinyin = s.TypePinyin;
        AutoAnalyze = s.AutoAnalyze;
        CardModeIndex = (int)s.CardMode;
        ItemsShownIndex = PageSizes.ToList().IndexOf(PageSizeOf(s.ItemsShown));
        PopupEnabled = s.PopupEnabled;
        PopupFrequencyIndex = s.PopupCustom ? CustomFrequencyIndex : FrequencyIndex(s.PopupMinSeconds, s.PopupMaxSeconds);
        CustomFromText = s.PopupMinSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);
        CustomToText = s.PopupMaxSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _loading = false;
    }

    /// <summary>Raised after a change is saved, with the property's name.</summary>
    public event EventHandler<string>? Changed;

    public VocabSettings Current => _settings.Current;

    /// <summary>How the Learn tab asks its cards now.</summary>
    public VocabCardOptions CardOptions => new(_settings.Current.CardMode, _settings.Current.AutoSay, _settings.Current.TypePinyin);

    public IReadOnlyList<string> CardModes => CardModeNames;

    /// <summary>The choices of "Words per page": a page is all that is ever drawn, so the lists stay quick.</summary>
    public static IReadOnlyList<int> PageSizes { get; } = [10, 20, 50, 100];

    public const int DefaultPageSize = 20;

    /// <summary>A saved page size, or the default for one from before pages (5 on the phone, "all" on the PC).</summary>
    public static int PageSizeOf(int saved) => PageSizes.Contains(saved) ? saved : DefaultPageSize;

    /// <summary>Rows per page of the Words and Roots lists.</summary>
    public int PageSize => PageSizeOf(_settings.Current.ItemsShown);

    public IReadOnlyList<string> ItemsShownNames { get; } = PageSizes.Select(n => n.ToString(System.Globalization.CultureInfo.CurrentCulture)).ToList();

    public IReadOnlyList<string> PopupFrequencyNames { get; } = [.. PopupFrequencies.Select(f => f.Name), "Custom (type the seconds)"];

    public bool IsCustomFrequency => PopupFrequencyIndex == CustomFrequencyIndex;

    /// <summary>Android: the apps the pop-up and the bubble stay out of (package names, in the order they were added).</summary>
    public IReadOnlyList<string> BlockedApps => _settings.Current.BlockedApps;

    /// <summary>Android: keeps the pop-up and the bubble out of these apps (or lets them back).</summary>
    public void SetBlockedApps(IEnumerable<string> packages)
    {
        var list = packages.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
        Save(nameof(BlockedApps), s => s.BlockedApps = list);
        OnPropertyChanged(nameof(BlockedApps));
    }

    /// <summary>Android: where the user left the bubble (fractions of the screen).</summary>
    public void SaveBubblePosition(double x, double y) =>
        Save(nameof(VocabSettings.BubbleX), s =>
        {
            s.BubbleX = Math.Clamp(x, 0, 1);
            s.BubbleY = Math.Clamp(y, 0, 1);
        });

    public bool HasCustomError => CustomError.Length > 0;

    partial void OnShowTraditionalChanged(bool value) => Save(nameof(ShowTraditional), s => s.ShowTraditional = value);

    partial void OnShowYouGlishChanged(bool value) => Save(nameof(ShowYouGlish), s => s.ShowYouGlish = value);

    partial void OnBubbleEnabledChanged(bool value) => Save(nameof(BubbleEnabled), s => s.BubbleEnabled = value);

    partial void OnAutoSayChanged(bool value) => Save(nameof(AutoSay), s => s.AutoSay = value);

    partial void OnTypePinyinChanged(bool value) => Save(nameof(TypePinyin), s => s.TypePinyin = value);

    partial void OnAutoAnalyzeChanged(bool value) => Save(nameof(AutoAnalyze), s => s.AutoAnalyze = value);

    partial void OnCardModeIndexChanged(int value) =>
        Save(nameof(CardModeIndex), s => s.CardMode = (VocabCardMode)Math.Clamp(value, 0, CardModeNames.Count - 1));

    partial void OnItemsShownIndexChanged(int value) =>
        Save(nameof(ItemsShownIndex), s => s.ItemsShown = PageSizes[Math.Clamp(value, 0, PageSizes.Count - 1)]);

    partial void OnPopupEnabledChanged(bool value) => Save(nameof(PopupEnabled), s => s.PopupEnabled = value);

    partial void OnPopupFrequencyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomFrequency));
        if (_loading) return;
        if (value == CustomFrequencyIndex)
        {
            SaveCustomRange();
            return;
        }
        var f = PopupFrequencies[Math.Clamp(value, 0, PopupFrequencies.Count - 1)];
        CustomError = "";
        Save(nameof(PopupFrequencyIndex), s =>
        {
            s.PopupMinSeconds = f.MinSeconds;
            s.PopupMaxSeconds = f.MaxSeconds;
            s.PopupCustom = false;
        });
    }

    partial void OnCustomFromTextChanged(string value) => SaveCustomRange();

    partial void OnCustomToTextChanged(string value) => SaveCustomRange();

    partial void OnCustomErrorChanged(string value) => OnPropertyChanged(nameof(HasCustomError));

    /// <summary>Saves the typed range once both numbers make sense; until then says what is wrong.</summary>
    private void SaveCustomRange()
    {
        if (_loading || !IsCustomFrequency) return;
        if (!int.TryParse(CustomFromText.Trim(), out var from) || !int.TryParse(CustomToText.Trim(), out var to))
        {
            CustomError = "Type whole numbers of seconds, e.g. 30 and 90.";
            return;
        }
        if (from < MinPopupSeconds)
        {
            CustomError = $"At least {MinPopupSeconds} seconds.";
            return;
        }
        if (to < from)
        {
            CustomError = "The second number is the longest gap: at least the first.";
            return;
        }
        if (to > MaxPopupSeconds)
        {
            CustomError = $"At most {MaxPopupSeconds} seconds (a day).";
            return;
        }
        CustomError = "";
        Save(nameof(PopupFrequencyIndex), s =>
        {
            s.PopupMinSeconds = from;
            s.PopupMaxSeconds = to;
            s.PopupCustom = true;
        });
    }

    /// <summary>The Words tab's order (chosen on that tab).</summary>
    public void SaveWordsSort(int index) => Save(nameof(VocabSettings.WordsSort), s => s.WordsSort = index);

    private void Save(string name, Action<VocabSettings> change)
    {
        if (_loading) return;
        _settings.Update(change);
        Changed?.Invoke(this, name);
    }

    private static int FrequencyIndex(int min, int max)
    {
        for (var i = 0; i < PopupFrequencies.Count; i++)
            if (PopupFrequencies[i].MinSeconds == min && PopupFrequencies[i].MaxSeconds == max) return i;
        return 1;
    }
}
