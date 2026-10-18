using Avalonia;
using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

/// <summary>Novel Reader's settings (Home → Utilities). Reading is on <see cref="NovelReaderContentPage"/>.</summary>
public partial class NovelReaderPage : ModulePageBase
{
    private const string Sample = "Lâm Uyển ngẩng đầu nhìn trời, khẽ thở dài một hơi.";

    private readonly NovelReaderViewModel _viewModel;
    private readonly INovelPlatform _platform;
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NovelReaderPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NovelReaderModule>(), HelmAndroidServices.Current.GetRequiredService<NovelReaderViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<INovelPlatform>(), HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public NovelReaderPage(NovelReaderModule module, NovelReaderViewModel viewModel, INovelPlatform platform, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _platform = platform;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _platform.StartPhoneVoices();
        ShowBatteryNote();
        ShowLens();
    }

    // ---- Novel Lens ------------------------------------------------------------------------------------------------

    private const string LensServiceName = "com.huyhung1404.helm/com.huyhung1404.helm.novel.LensService";

    private Lens.LensTranslator? _lens;
    private List<string?> _lensBookIds = [];
    private bool _showingLens;

    /// <summary>Whether Novel Lens is on, the novel its names come from, and where Next taps (read again on every visit).</summary>
    private void ShowLens()
    {
        _lens ??= HelmAndroidServices.Current.GetService<Lens.LensTranslator>();
        if (_lens is null) return;
        _showingLens = true;
        try
        {
            var context = global::Android.App.Application.Context;
            if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                LensState.Text = "Novel Lens needs Android 11 or later.";
                LensButton.IsVisible = false;
            }
            else
            {
                var enabled = global::Android.Provider.Settings.Secure.GetString(context.ContentResolver,
                    global::Android.Provider.Settings.Secure.EnabledAccessibilityServices) ?? "";
                var on = enabled.Split(':').Any(s => s.Equals(LensServiceName, StringComparison.OrdinalIgnoreCase));
                LensState.Text = on
                    ? "On. Open Fanqie and press the 译 button."
                    : "Off. Turn on \"Helm Novel Lens\" in Accessibility settings (under Downloaded or Installed apps).";
                LensButton.Content = on ? "Accessibility settings" : "Turn on Novel Lens";
            }

            var books = _lens.Books();
            _lensBookIds = [null, .. books.Select(b => (string?)b.Id)];
            LensBook.ItemsSource = new List<string> { "The novel open in Novel Reader" }.Concat(books.Select(b => b.Title)).ToList();
            LensBook.SelectedIndex = Math.Max(0, _lensBookIds.IndexOf(_lens.Settings.LensBookId));
            LensTap.Value = _lens.Settings.LensNextTap;
            ShowLensTap();
        }
        finally
        {
            _showingLens = false;
        }
    }

    private void ShowLensTap() => LensTapLabel.Text = $"Next taps at {LensTap.Value:P0} of the width";

    private void LensSettings_Click(object? sender, RoutedEventArgs e)
    {
        var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionAccessibilitySettings);
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        global::Android.App.Application.Context.StartActivity(intent);
    }

    /// <summary>A novel with just the typed title, chosen for the lens (the names fixed on the lens go to it).</summary>
    private void LensCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_lens is null || LensNewTitle.Text is not { } title || title.Trim().Length == 0) return;
        _lens.CreateBook(title);
        LensNewTitle.Text = "";
        ShowLens();
    }

    private void LensBook_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_showingLens || _lens is null || LensBook.SelectedIndex < 0 || LensBook.SelectedIndex >= _lensBookIds.Count) return;
        var id = _lensBookIds[LensBook.SelectedIndex];
        _lens.Save(s => s.LensBookId = id);
    }

    private void LensTap_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LensTapLabel is null) return;
        ShowLensTap();
        if (_showingLens || _lens is null) return;
        var value = Math.Round(LensTap.Value, 2);
        _lens.Save(s => s.LensNextTap = value);
    }

    private void OpenReader_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(NovelReaderContentPage));

    private async void ImportDictionaries_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await _platform.PickDictionariesAsync(CancellationToken.None);
            if (paths.Count > 0) await _viewModel.ImportDictionariesAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Message = "Could not import: " + ex.Message;
        }
    }

    /// <summary>A sentence with the chosen voice: the phone's voices at once; HoaiMy needs a novel open to read from.</summary>
    private void Listen_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVoice is { IsOnPhone: true } voice)
        {
            ListenNote.IsVisible = false;
            _platform.PreviewPhoneVoice(voice, Sample);
            return;
        }
        ListenNote.Text = "Open a novel and press play to hear this voice.";
        ListenNote.IsVisible = true;
    }

    private void InstallVoices_Click(object? sender, RoutedEventArgs e) => _platform.InstallVoiceData();

    private void VoiceSettings_Click(object? sender, RoutedEventArgs e) => _platform.OpenVoiceSettings();

    private void Battery_Click(object? sender, RoutedEventArgs e) => _platform.OpenBatterySettings();

    private void ShowBatteryNote() =>
        BatteryNote.Text = _platform.RunsFreelyInBackground
            ? "Battery optimization is off for Helm: Android lets it read in the background."
            : "If reading stops after a while with the screen off, turn off battery optimization for Helm (some phones, e.g. Xiaomi or Oppo, also need Helm allowed to run in the background in their own settings).";
}
