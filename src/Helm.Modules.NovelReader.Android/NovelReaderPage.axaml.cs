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

    /// <summary>The key goes from the box to the view model once, and the box is emptied.</summary>
    private void SaveClaudeKey_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetClaudeKey(ClaudeKeyBox.Text ?? "");
        ClaudeKeyBox.Text = "";
    }

    private void InstallVoices_Click(object? sender, RoutedEventArgs e) => _platform.InstallVoiceData();

    private void VoiceSettings_Click(object? sender, RoutedEventArgs e) => _platform.OpenVoiceSettings();

    private void Battery_Click(object? sender, RoutedEventArgs e) => _platform.OpenBatterySettings();

    private void ShowBatteryNote() =>
        BatteryNote.Text = _platform.RunsFreelyInBackground
            ? "Battery optimization is off for Helm: Android lets it read in the background."
            : "If reading stops after a while with the screen off, turn off battery optimization for Helm (some phones, e.g. Xiaomi or Oppo, also need Helm allowed to run in the background in their own settings).";
}
