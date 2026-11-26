using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Helm.Modules.WindowZones.Engine;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>
/// The tab strip of a zone (see the .axaml): built from what the engine hands over (<see cref="TabStripView"/>). Never
/// wider than the zone: the tabs share what is left after the label, each between <see cref="MinTabWidth"/> and
/// <see cref="TabWidth"/>.
/// </summary>
public partial class ZoneTabStripView : UserControl
{
    public const double TabWidth = 220, MinTabWidth = 90;

    /// <summary>Room around the card for its shadow (its margin), on each side.</summary>
    public const double ShadowSpace = 16;

    /// <summary>What the label and the card's padding take from the zone's width.</summary>
    private const double LabelRoom = 120;

    public ZoneTabStripView() => InitializeComponent();

    /// <summary>A tab was clicked: bring that window forward.</summary>
    public event EventHandler<nint>? TabChosen;

    /// <summary>What it shows now.</summary>
    public TabStripView? View { get; private set; }

    /// <summary>Shows the zone's tabs. <paramref name="icons"/> gives a program's icon from its .exe (null: a generic glyph).</summary>
    public void Show(TabStripView view, Func<string, IImage?> icons)
    {
        View = view;
        Label.Text = view.Label;
        while (Row.Children.Count > 1) Row.Children.RemoveAt(1);
        var width = TabWidthFor(view);
        foreach (var window in view.Windows) Row.Children.Add(Tab(window, window.Handle == view.Current, width, icons));
    }

    /// <summary>Each tab's width (device-independent pixels on the zone's monitor).</summary>
    public static double TabWidthFor(TabStripView view)
    {
        var room = view.Zone.Width / view.Monitor.Scale - 2 * ShadowSpace - LabelRoom;
        return Math.Clamp(room / Math.Max(1, view.Windows.Count), MinTabWidth, TabWidth);
    }

    private Button Tab(ZoneWindowSnapshot window, bool current, double width, Func<string, IImage?> icons)
    {
        var title = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        Control icon;
        if (!string.IsNullOrEmpty(window.ProcessPath) && icons(window.ProcessPath) is { } image)
        {
            icon = new Image { Source = image, Width = 16, Height = 16 };
        }
        else
        {
            var glyph = new SymbolIcon { Symbol = Symbol.Apps, FontSize = 15 };
            glyph.Bind(ForegroundProperty, glyph.GetResourceObservable("HelmSecondaryText"));
            icon = glyph;
        }
        icon.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        content.Children.Add(icon);
        var text = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (current) text.FontWeight = FontWeight.SemiBold;
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var tab = new Button
        {
            Width = width,
            MinHeight = 36,
            Height = 36,
            Padding = new Thickness(10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Content = content,
            Tag = window.Handle,
        };
        tab.Classes.Add("chip");
        if (current) tab.Classes.Add("selected");
        // A minimized window is still in the zone, just not on screen.
        if (window.IsMinimized && !current) tab.Opacity = 0.6;
        ToolTip.SetTip(tab, title);
        AutomationProperties.SetName(tab, title);
        tab.Click += (_, _) => TabChosen?.Invoke(this, window.Handle);
        return tab;
    }
}
