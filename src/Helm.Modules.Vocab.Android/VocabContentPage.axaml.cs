using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the words stay visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class VocabContentPage : UserControl
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public VocabContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<VocabModule>(), HelmAndroidServices.Current.GetRequiredService<VocabViewModel>())
    {
    }

    public VocabContentPage(VocabModule module, VocabViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Holding a box of a tree offers to make that part one of the words.
        TreeNodeHold.Attach(this);
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        ShellIntents.ItemOffered += OnItemOffered;
        _viewModel.List.Pages.Turned += OnWordsPageTurned;
        _viewModel.Roots.Pages.Turned += OnRootsPageTurned;
        ApplyEnabled();
        OpenOfferedWord();
        // Opening Vocabulary is the moment to ask for notifications (Android 13+, once) for the lock-screen word.
        if (_viewModel.Settings.LockScreenWord) VocabLockScreen.AskPermissionOnce(Android.App.Application.Context);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        ShellIntents.ItemOffered -= OnItemOffered;
        _viewModel.List.Pages.Turned -= OnWordsPageTurned;
        _viewModel.Roots.Pages.Turned -= OnRootsPageTurned;
        base.OnDetachedFromVisualTree(e);
    }

    // A new page starts at the top of its list.
    private void OnWordsPageTurned(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => WordsList.BringIntoView(new Avalonia.Rect(0, 0, 1, 1)));

    private void OnRootsPageTurned(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => RootsList.BringIntoView(new Avalonia.Rect(0, 0, 1, 1)));

    private void OnItemOffered(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OpenOfferedWord);

    /// <summary>A tap on the widget's parts: that word with its whole tree.</summary>
    private void OpenOfferedWord()
    {
        if (ShellIntents.TakeItem(VocabIds.ModuleId) is { } wordId) _viewModel.Open(wordId);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
