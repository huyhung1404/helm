using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Helm.Core.Ui;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// The top of a Wallet sheet: what it is about as the eyebrow line, its title (20 semibold), a line under it, and the
/// close chip; optionally a picture on the left (a category badge, a person's initial).
/// </summary>
public sealed class SheetHeader : Grid
{
    public static readonly StyledProperty<string?> EyebrowProperty = AvaloniaProperty.Register<SheetHeader, string?>(nameof(Eyebrow));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SheetHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<SheetHeader, string?>(nameof(Subtitle));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty = AvaloniaProperty.Register<SheetHeader, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<Control?> LeadingProperty = AvaloniaProperty.Register<SheetHeader, Control?>(nameof(Leading));

    private readonly TextBlock _eyebrow = new() { Classes = { "eyebrow" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { Classes = { "muted" }, FontSize = 13 };
    private readonly ContentControl _leading = new() { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new()
    {
        Classes = { "chip" },
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(10, 0, 0, 0),
        Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Dismiss, FontSize = 14 },
    };

    public SheetHeader()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Margin = new Thickness(0, 0, 0, 4);
        ToolTip.SetTip(_close, "Close");
        Avalonia.Automation.AutomationProperties.SetName(_close, "Close");
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _eyebrow, _title, _subtitle } };
        SetColumn(text, 1);
        SetColumn(_close, 2);
        Children.Add(_leading);
        Children.Add(text);
        Children.Add(_close);
        Update();
    }

    public string? Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public Control? Leading
    {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EyebrowProperty || change.Property == TitleProperty || change.Property == SubtitleProperty
            || change.Property == CloseCommandProperty || change.Property == LeadingProperty)
            Update();
    }

    private void Update()
    {
        _eyebrow.Text = UpperCase.Instance.Convert(Eyebrow, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "";
        _eyebrow.IsVisible = !string.IsNullOrEmpty(Eyebrow);
        _title.Text = Title ?? "";
        _subtitle.Text = Subtitle ?? "";
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _close.Command = CloseCommand;
        _close.IsVisible = CloseCommand is not null;
        _leading.Content = Leading;
        _leading.IsVisible = Leading is not null;
    }
}
