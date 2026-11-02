using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Helm.Modules.Vocab.Views;

/// <summary>
/// The Analysis dialog (see the XAML). The Activity log follows the run: a new line scrolls it to the end, unless the
/// user scrolled up to read an earlier one.
/// </summary>
public partial class VocabAgentDialog : UserControl
{
    private VocabAgentViewModel? _agent;

    public VocabAgentDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Follow((DataContext as VocabViewModel)?.Agent);
    }

    private void Follow(VocabAgentViewModel? agent)
    {
        if (_agent is not null) _agent.Log.CollectionChanged -= OnLogChanged;
        _agent = agent;
        if (_agent is not null) _agent.Log.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // At the end (or nearly) before the line came: stay there. A new run (the log cleared) starts at the top.
        var atEnd = LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 24;
        if (e.Action == NotifyCollectionChangedAction.Reset) LogScroller.ScrollToTop();
        else if (atEnd) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LogScroller.ScrollToEnd);
    }
}
