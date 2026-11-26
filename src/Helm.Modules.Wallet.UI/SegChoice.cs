using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// One of a few choices (Spending · Money in · Transfer) as the segmented tabs of a glass page, filling the row in equal
/// parts. <see cref="SelectedIndex"/> binds both ways; keyboard and screen readers get radio buttons.
/// </summary>
public sealed class SegChoice : Border
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ItemsProperty =
        AvaloniaProperty.Register<SegChoice, IReadOnlyList<string>?>(nameof(Items));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SegChoice, int>(nameof(SelectedIndex), defaultBindingMode: BindingMode.TwoWay);

    private static int _groups;
    private readonly UniformGrid _row = new() { Rows = 1 };
    private readonly string _group = "SegChoice" + Interlocked.Increment(ref _groups);
    private bool _syncing;

    public SegChoice()
    {
        // A faint well (not a glass card): the chosen part, lighter, stands out on a sheet too.
        Padding = new Thickness(3);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, this.GetResourceObservable("HelmSubtleFill"));
        Bind(BorderBrushProperty, this.GetResourceObservable("HelmGlassStroke"));
        Child = _row;
    }

    public IReadOnlyList<string>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty) Build();
        else if (change.Property == SelectedIndexProperty) Sync();
    }

    private void Build()
    {
        _row.Children.Clear();
        var items = Items ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var button = new RadioButton
            {
                Classes = { "seg" },
                GroupName = _group,
                Content = new TextBlock { Text = items[i], TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Avalonia.Automation.AutomationProperties.SetName(button, items[i]);
            button.IsCheckedChanged += (_, _) =>
            {
                if (!_syncing && button.IsChecked == true) SelectedIndex = index;
            };
            _row.Children.Add(button);
        }
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        for (var i = 0; i < _row.Children.Count; i++)
            if (_row.Children[i] is RadioButton r) r.IsChecked = i == SelectedIndex;
        _syncing = false;
    }
}
