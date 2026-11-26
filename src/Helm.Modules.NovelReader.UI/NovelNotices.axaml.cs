using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The notices over the library and over an open novel (see the XAML); <see cref="InReader"/> picks the short ones.</summary>
public partial class NovelNotices : UserControl
{
    public static readonly StyledProperty<bool> InReaderProperty = AvaloniaProperty.Register<NovelNotices, bool>(nameof(InReader));

    private NovelReaderViewModel? _viewModel;

    public NovelNotices() => InitializeComponent();

    /// <summary>Over an open novel: a one-line note about the dictionaries instead of the download card.</summary>
    public bool InReader
    {
        get => GetValue(InReaderProperty);
        set => SetValue(InReaderProperty, value);
    }

    /// <summary>The tool is off: the novels stay, read only.</summary>
    public void ShowOff(bool off) => OffBar.IsVisible = off;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnChanged;
        _viewModel = DataContext as NovelReaderViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnChanged;
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InReaderProperty) Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NovelReaderViewModel.NeedsDictionaries) or nameof(NovelReaderViewModel.HasBooks)) Update();
    }

    private void Update()
    {
        var needs = _viewModel?.NeedsDictionaries == true;
        // The first run's card asks for them itself.
        DictionaryCard.IsVisible = needs && !InReader && _viewModel?.HasBooks == true;
        DictionaryNote.IsVisible = needs && InReader;
    }
}
