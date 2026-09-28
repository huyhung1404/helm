using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the lists stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class TrackerContentPage : Page
{
    private readonly TrackerModule _module;

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _module = module;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _grouping;

    /// <summary>Thousands separators while an amount is typed, so 1500000 reads 1,500,000; the caret stays put.</summary>
    private void OnAmountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_grouping || sender is not TextBox box) return;
        var (text, caret) = TrackerFormat.GroupDigits(box.Text, box.CaretIndex);
        if (text == box.Text) return;
        _grouping = true;
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            _grouping = false;
        }
    }
}
