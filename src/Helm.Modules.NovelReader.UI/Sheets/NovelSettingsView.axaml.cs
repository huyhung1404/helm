using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>Novel Reader's settings, shown as a sheet over the page (see <see cref="NovelReaderView"/>'s gear).</summary>
public partial class NovelSettingsView : UserControl
{
    private const string Sample = "Lâm Uyển ngẩng đầu nhìn trời, khẽ thở dài một hơi.";

    private readonly NovelReaderViewModel? _viewModel;
    private readonly INovelReaderPageHost? _host;
    private bool _sourcesOpen;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelSettingsView() => InitializeComponent();

    public NovelSettingsView(NovelReaderViewModel viewModel, IModule module, INovelReaderPageHost host, Action close)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();

        // What the platform has.
        OpenFolderButton.IsVisible = host.CanOpenFolders;
        PointerSettings.IsVisible = host.HasPointer;
        SystemVoiceButtons.IsVisible = host.CanAddSystemVoices;
        PhoneVoiceButtons.IsVisible = host.Phone is not null;
        BackgroundCard.IsVisible = host.Phone is not null;
        ScrapeSettings.IsVisible = host.Scraper is not null;
        NamesCard.IsVisible = host.CanUseTextFiles;
        NextChapterHint.Text = host.Phone is not null
            ? "Keeps reading chapter after chapter, also with the screen off."
            : "At the end of a chapter, reading aloud opens the next one and goes on.";
        VoiceHint.Text = host.Phone is not null
            ? "VieNeu's voices once they are downloaded, and the phone's own Vietnamese voices. Sound is held in memory while you listen, never saved."
            : host.CanAddSystemVoices
                ? "VieNeu's voices once they are downloaded, and the Windows voices. All of them read offline and send nothing."
                : "VieNeu's voices once they are downloaded, and this device's own voices.";
        if (host.Phone is { } phone)
            BatteryNote.Text = phone.RunsFreelyInBackground
                ? "Battery optimization is off for Helm: Android lets it read in the background."
                : "If reading stops after a while with the screen off, turn off battery optimization for Helm (some phones, e.g. Xiaomi or Oppo, also need Helm allowed to run in the background in their own settings).";
        if (host.Lens is { } lens) LensArea.Content = new NovelLensCard(lens, viewModel);

        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) =>
        {
            module.PropertyChanged += OnModule;
        };
        DetachedFromVisualTree += (_, _) => module.PropertyChanged -= OnModule;
    }

    private async void ImportDictionaries_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        try
        {
            var paths = await _host.PickDictionariesAsync();
            if (paths.Count > 0) await _viewModel.ImportDictionariesAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Message = "Could not import: " + ex.Message;
        }
    }

    private void Sources_Click(object? sender, RoutedEventArgs e)
    {
        _sourcesOpen = !_sourcesOpen;
        Motion.SetTurn(SourcesChevron, _sourcesOpen);
        SourcesPanel.IsVisible = _sourcesOpen;
    }

    private void Sources_LostFocus(object? sender, RoutedEventArgs e) => _viewModel?.ApplyDictionarySources(SourcesBox.Text ?? "");

    /// <summary>A sentence with the chosen voice: the phone's voices at once; VieNeu's need a novel open to read from.</summary>
    private void Listen_Click(object? sender, RoutedEventArgs e)
    {
        if (_host?.Phone is not { } phone || _viewModel is null) return;
        if (_viewModel.SelectedVoice is { IsOnPhone: true } voice)
        {
            ListenNote.IsVisible = false;
            phone.PreviewVoice(voice, Sample);
            return;
        }
        ListenNote.Text = "Open a novel and press play to hear this voice.";
        ListenNote.IsVisible = true;
    }

    private void InstallVoices_Click(object? sender, RoutedEventArgs e) => _host?.Phone?.InstallVoiceData();

    private void VoiceSettings_Click(object? sender, RoutedEventArgs e) => _host?.Phone?.OpenVoiceSettings();

    private void Battery_Click(object? sender, RoutedEventArgs e) => _host?.Phone?.OpenBatterySettings();

    private async void ExportNames_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        if (await _host.PickSaveFileAsync("Export names for all novels", "Names.txt", ".txt") is { } path) _viewModel.ExportEntriesTo(null, EntryKind.Name, path);
    }

    private async void ImportNames_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        if (await _host.PickTextFileAsync("Import names for all novels") is { } path) _viewModel.ImportEntries(null, EntryKind.Name, path);
    }
}
