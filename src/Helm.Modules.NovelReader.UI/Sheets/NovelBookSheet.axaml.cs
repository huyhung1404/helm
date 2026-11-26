using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// A novel's sheet (see the XAML), from "Edit" on its card. It edits the card's drafts (title, web source) through the
/// view model's rename and source commands, and changes the cover, the source's switches and deletes at once.
/// </summary>
public partial class NovelBookSheet : UserControl
{
    private readonly BookCardViewModel? _card;
    private bool _showing;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelBookSheet() => InitializeComponent();

    public NovelBookSheet(NovelReaderViewModel viewModel, BookCardViewModel card, INovelReaderPageHost host, Action close)
    {
        _card = card;
        DataContext = card;
        InitializeComponent();
        viewModel.StartRenameCommand.Execute(card);
        viewModel.StartEditSourceCommand.Execute(card);

        CloseButton.Click += (_, _) => close();
        CancelButton.Click += (_, _) => close();
        SaveButton.Click += (_, _) =>
        {
            viewModel.SaveRenameCommand.Execute(card);
            viewModel.SaveSourceCommand.Execute(card);
            // A source that is not a web address stays open, with the message saying why.
            if (!card.IsEditingSource) close();
        };
        ChangeCoverButton.Click += async (_, _) =>
        {
            try
            {
                if (await host.PickCoverAsync(card.Title) is { } picture) await viewModel.SetCoverAsync(card, picture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                viewModel.Message = "Could not set the cover: " + ex.Message;
            }
        };
        RemoveCoverButton.Click += (_, _) => viewModel.RemoveCoverCommand.Execute(card);
        AutoCheckSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_showing && AutoCheckSwitch.IsChecked != card.AutoCheck) viewModel.ToggleAutoCheckCommand.Execute(card);
        };
        AutoSaveSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_showing && AutoSaveSwitch.IsChecked != card.AutoSave) viewModel.ToggleAutoSaveCommand.Execute(card);
        };
        // The PC downloads the new chapters and logs in to the site; the phone asks the PC.
        NewChaptersText.Text = host.Scraper is null ? "Get new chapters (on the PC)" : "Continue download";
        NewChaptersButton.Click += (_, _) =>
        {
            close();
            if (host.Scraper is { } scraper) scraper.ContinueCommand.Execute(card);
            else viewModel.RequestUpdateCommand.Execute(card);
        };
        LogInButton.IsVisible = host.Scraper is not null;
        LogInButton.Click += (_, _) => host.Scraper?.LogInCommand.Execute(card);
        _twoSourceButtons = host.Scraper is not null;
        DeleteButton.Click += (_, _) =>
        {
            close();
            viewModel.DeleteBookCommand.Execute(card);
        };
        AttachedToVisualTree += (_, _) =>
        {
            card.PropertyChanged += OnCardChanged;
            ShowSwitches();
            Arrange();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            card.PropertyChanged -= OnCardChanged;
            // Closed without Save: the drafts are dropped.
            viewModel.CancelRenameCommand.Execute(card);
            viewModel.CancelEditSourceCommand.Execute(card);
        };
    }

    private bool _twoSourceButtons;

    /// <summary>The source's two buttons share a row when they fit; on a phone each has its own.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var stack = !_twoSourceButtons || e.NewSize.Width < 440;
        Grid.SetColumnSpan(NewChaptersButton, stack ? 2 : 1);
        Grid.SetColumnSpan(LogInButton, stack ? 2 : 1);
        Grid.SetColumn(LogInButton, stack ? 0 : 1);
        Grid.SetRow(LogInButton, stack ? 1 : 0);
        LogInButton.Margin = stack ? new Avalonia.Thickness(0, 8, 0, 0) : default;
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BookCardViewModel.AutoCheck) or nameof(BookCardViewModel.AutoSave)) Dispatcher.UIThread.Post(ShowSwitches);
        if (e.PropertyName == nameof(BookCardViewModel.Cover)) Arrange();
    }

    private void ShowSwitches()
    {
        if (_card is null) return;
        _showing = true;
        AutoCheckSwitch.IsChecked = _card.AutoCheck;
        AutoSaveSwitch.IsChecked = _card.AutoSave;
        _showing = false;
    }

    /// <summary>Change cover alone fills its row; with Remove they share it.</summary>
    private void Arrange() => Grid.SetColumnSpan(ChangeCoverButton, _card?.HasCover == true ? 1 : 2);
}
