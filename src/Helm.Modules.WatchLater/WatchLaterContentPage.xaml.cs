using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Helm.Core.Modules;
using Helm.Modules.WatchLater.Player;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Watch Later itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the saved videos stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// A link dropped on the page is put in the box.
/// </summary>
public partial class WatchLaterContentPage : Page, IPlayerPanel
{
    private readonly WatchLaterModule _module;
    private readonly WatchLaterViewModel _viewModel;
    private readonly PlayerService _player;
    private bool _settingRate;

    public WatchLaterContentPage(WatchLaterModule module, WatchLaterViewModel viewModel, PlayerService player)
    {
        _module = module;
        _viewModel = viewModel;
        _player = player;
        DataContext = viewModel;
        InitializeComponent();
        foreach (var speed in PlayerView.Speeds) SpeedBox.Items.Add(PlayerWindow.SpeedText(speed));
        InlinePlayer.StatusChanged += (_, text) => PlayerStatus.Text = text;
        player.AttachPanel(this);
        // The video keeps a 16:9 shape, but leaves room for the list below it.
        SizeChanged += (_, _) => FitPlayer();
        // Leaving the page pauses the video (where it was is saved).
        Unloaded += (_, _) => player.PausePanel();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new FocusCommand(() => { LinkBox.Focus(); LinkBox.SelectAll(); }), Key.L, ModifierKeys.Control));
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        ApplyEnabled();
    }

    // ---- The player on the page (IPlayerPanel) ---------------------------------------------------------------------

    PlayerView IPlayerPanel.View => InlinePlayer;

    void IPlayerPanel.ShowPlayer(string title)
    {
        PlayerTitle.Text = title;
        PlayerPanel.Visibility = Visibility.Visible;
        FitPlayer();
    }

    void IPlayerPanel.HidePlayer() => PlayerPanel.Visibility = Visibility.Collapsed;

    void IPlayerPanel.ShowRate(double rate)
    {
        _settingRate = true;
        SpeedBox.SelectedIndex = Math.Max(0, PlayerView.Speeds.ToList().FindIndex(s => Math.Abs(s - rate) < 0.01));
        _settingRate = false;
    }

    private void FitPlayer()
    {
        var width = Math.Max(0, PlayerPanel.ActualWidth > 0 ? PlayerPanel.ActualWidth : ActualWidth - 48);
        InlinePlayer.Height = Math.Max(200, Math.Min(width * 9 / 16, ActualHeight * 0.55));
    }

    private void Speed_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingRate || SpeedBox.SelectedIndex < 0) return;
        _player.SetRate(PlayerView.Speeds[SpeedBox.SelectedIndex]);
    }

    private void PlayerWatched_Click(object sender, RoutedEventArgs e) => InlinePlayer.MarkWatched();

    private void PlayerBrowser_Click(object sender, RoutedEventArgs e) => InlinePlayer.OpenInBrowser();

    private void PlayerMini_Click(object sender, RoutedEventArgs e) => _player.PopOut();

    private void PlayerClose_Click(object sender, RoutedEventArgs e) => _player.StopPanel();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _module.IsEnabled && DroppedText(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!_module.IsEnabled || DroppedText(e) is not { } text) return;
        _viewModel.Paste(text);
        LinkBox.Focus();
        LinkBox.CaretIndex = LinkBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>A link dragged from a browser's address bar or a page.</summary>
    private static string? DroppedText(DragEventArgs e)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", DataFormats.UnicodeText, DataFormats.Text })
        {
            try
            {
                if (!e.Data.GetDataPresent(format)) continue;
                var data = e.Data.GetData(format);
                var text = data switch
                {
                    string s => s,
                    MemoryStream m => System.Text.Encoding.Unicode.GetString(m.ToArray()).TrimEnd('\0'),
                    _ => null,
                };
                if (VideoLink.Find(text) is not null) return text;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        }
        return null;
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        Top.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class FocusCommand(Action focus) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => focus();
    }
}

/// <summary>Visible when every bound value is true.</summary>
public sealed class AllTrueToVisibility : IMultiValueConverter
{
    public static AllTrueToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
