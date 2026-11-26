using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Ui;
using Helm.Shell.Services;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Lens;
using Helm.Modules.NovelReader.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared <see cref="NovelReaderView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>: the
/// text scrolls itself and the player stays at the bottom). This page gives it what only the phone has: the pickers, the
/// phone's own voices and battery settings, and Novel Lens. Back closes a sheet first, then leaves a novel for the library
/// (see <see cref="NovelReaderModule.HandleBack"/>). The page is transient; reading aloud lives in the view model and goes on
/// when the page is gone.
/// </summary>
public sealed class NovelReaderContentPage : UserControl, IFullPage, INovelReaderPageHost
{
    private readonly NovelReaderModule _module;
    private readonly INovelPlatform _platform;
    private readonly NovelReaderView _view;
    private readonly INovelPhone _phone;
    private readonly INovelLens? _lens;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NovelReaderContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NovelReaderModule>(), HelmAndroidServices.Current.GetRequiredService<NovelReaderViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<INovelPlatform>(), HelmAndroidServices.Current.GetRequiredService<IClipboardService>(),
            HelmAndroidServices.Current.GetService<LensTranslator>())
    {
    }

    public NovelReaderContentPage(NovelReaderModule module, NovelReaderViewModel viewModel, INovelPlatform platform, IClipboardService clipboard,
        LensTranslator? lens)
    {
        _module = module;
        _platform = platform;
        _phone = new PhoneAdapter(platform);
        _lens = lens is null ? null : new LensAdapter(lens);
        _view = new NovelReaderView(module, viewModel, this, clipboard);
        Content = _view;
    }

    public bool HasPointer => false;

    public INovelPhone? Phone => _phone;

    public INovelLens? Lens => _lens;

    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;

    /// <summary>The phone's own voices start when Novel Reader is opened, and again when the app comes back to it.</summary>
    public void Opened() => _platform.StartPhoneVoices();

    public Task<string?> PickNovelAsync() => _platform.PickNovelAsync(CancellationToken.None);

    public Task<byte[]?> PickCoverAsync(string bookTitle) => _platform.PickCoverAsync(CancellationToken.None);

    public Task<IReadOnlyList<string>> PickDictionariesAsync() => _platform.PickDictionariesAsync(CancellationToken.None);

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PageBack = _view.HandleBack;
        ActivityHost.Resumed += OnResumed;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        if (_module.PageBack == _view.HandleBack) _module.PageBack = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_view.Shown);

    /// <summary>The phone's voices and battery settings, behind <see cref="INovelPlatform"/>.</summary>
    private sealed class PhoneAdapter(INovelPlatform platform) : INovelPhone
    {
        public void PreviewVoice(Speech.SpeechVoice voice, string text) => platform.PreviewPhoneVoice(voice, text);

        public void InstallVoiceData() => platform.InstallVoiceData();

        public void OpenVoiceSettings() => platform.OpenVoiceSettings();

        public void OpenBatterySettings() => platform.OpenBatterySettings();

        public bool RunsFreelyInBackground => platform.RunsFreelyInBackground;
    }

    /// <summary>
    /// Novel Lens (<see cref="LensComponent"/>, the accessibility service) as the settings sheet shows it: hidden (not listed
    /// as an accessibility service, so banking apps do not warn about Helm), almost on, or on; the novel its names come from
    /// and where Next taps Fanqie.
    /// </summary>
    private sealed class LensAdapter(LensTranslator lens) : INovelLens
    {
        private static global::Android.Content.Context Context => global::Android.App.Application.Context;

        private List<string?> _bookIds = [];

        public string State { get; private set; } = "";

        public bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(30);

        public bool IsShown { get; private set; }

        public IReadOnlyList<string> Books { get; private set; } = [];

        public int BookIndex
        {
            get => Math.Max(0, _bookIds.IndexOf(lens.Settings.LensBookId));
            set
            {
                if (value < 0 || value >= _bookIds.Count) return;
                var id = _bookIds[value];
                lens.Save(s => s.LensBookId = id);
            }
        }

        public double NextTap
        {
            get => lens.Settings.LensNextTap;
            set
            {
                var tap = Math.Round(value, 2);
                lens.Save(s => s.LensNextTap = tap);
            }
        }

        public void Refresh()
        {
            if (!IsSupported)
            {
                State = "Novel Lens needs Android 11 or later.";
                IsShown = false;
            }
            else
            {
                IsShown = LensComponent.IsShown(Context);
                var on = IsShown && LensComponent.IsSwitchedOn(Context);
                State = !IsShown
                    ? "Hidden. Banking apps can't see it. Turn it on to read Fanqie, and off again when you're done."
                    : on
                        ? "On. Open Fanqie and press the 译 button. Turn it off before you open a banking app: they warn about apps that have an accessibility service."
                        : "Almost: turn on \"Helm Novel Lens\" in Accessibility settings (under Downloaded or Installed apps).";
            }
            var books = lens.Books();
            _bookIds = [null, .. books.Select(b => (string?)b.Id)];
            Books = new List<string> { "The novel open in Novel Reader" }.Concat(books.Select(b => b.Title)).ToList();
        }

        /// <summary>Lists Novel Lens again (if hidden) and opens Accessibility settings to switch it on.</summary>
        public void TurnOn()
        {
            LensComponent.SetShown(Context, true);
            var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }

        /// <summary>Hides Novel Lens: Android stops it and it leaves Accessibility settings (and banking apps' scans).</summary>
        public void TurnOff() => LensComponent.SetShown(Context, false);

        /// <summary>A novel with just the typed title, chosen for the lens (the names fixed on the lens go to it).</summary>
        public void CreateBook(string title) => lens.CreateBook(title);
    }
}
