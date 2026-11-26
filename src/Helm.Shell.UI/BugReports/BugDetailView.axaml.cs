using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Core.Ui;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>
/// One bug (<see cref="BugDetailViewModel"/>), the same on every platform. Closing and reopening ask in a sheet
/// (<see cref="BugCloseView"/>, <see cref="BugReopenView"/>); a black box and a picture open in a sheet too. Pictures for
/// details and answers come from the platform (<see cref="Host"/>).
/// </summary>
public partial class BugDetailView : UserControl
{
    private SheetHandle? _sheet;

    public BugDetailView()
    {
        InitializeComponent();
        // Thumbnails are in shared templates: their clicks are handled here, for every list of them.
        AddHandler(Button.ClickEvent, OnButtonClick);
        // Another bug opened: what was asked about the last one goes.
        DataContextChanged += (_, _) => CloseSheet();
    }

    /// <summary>Where pictures come from (and open full size). Set by the page.</summary>
    public IBugPictureSource? Host { get; set; }

    private BugDetailViewModel? Detail => DataContext as BugDetailViewModel;

    /// <summary>The sheet open over the bug (close, reopen, a black box, a picture), while it is open.</summary>
    public SheetHandle? OpenSheet => _sheet is { IsOpen: true } ? _sheet : null;

    private SheetHandle? Show(Control body, double width)
    {
        CloseSheet();
        _sheet = Sheet.Show(this, body, width);
        return _sheet;
    }

    // A sheet belongs to the page: it goes when the page goes (another page, the window closing).
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        CloseSheet();
        base.OnDetachedFromVisualTree(e);
    }

    private void CloseSheet()
    {
        var sheet = _sheet;
        _sheet = null;
        sheet?.Close();
    }

    // ---- closing, reopening ----

    /// <summary>"Close #B12?": why, then the bug is closed.</summary>
    public SheetHandle? AskClose()
    {
        if (Detail is not { } detail) return null;
        return Show(new BugCloseView(detail.Code, async answer =>
        {
            CloseSheet();
            if (answer is not null) await detail.CloseAsync(answer.Reason, answer.Note, answer.DuplicateOf);
        }), 460);
    }

    /// <summary>"Reopen #B12?" (or "still broken?" from a fix to check): why, then it is open again.</summary>
    public SheetHandle? AskReopen(bool stillBroken)
    {
        if (Detail is not { } detail) return null;
        return Show(new BugReopenView(detail.Code, stillBroken, async answer =>
        {
            CloseSheet();
            if (answer is not null) await detail.ReopenAsync(answer.Why, answer.WithBlackBox);
        }), 460);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => AskClose();

    private void Reopen_Click(object? sender, RoutedEventArgs e) => AskReopen(stillBroken: false);

    private void StillBroken_Click(object? sender, RoutedEventArgs e) => AskReopen(stillBroken: true);

    // ---- black boxes ----

    /// <summary>The newest black box of the bug, every event and the environment.</summary>
    public SheetHandle? ShowBlackBox()
    {
        if (Detail is not { HasBlackBox: true } detail) return null;
        var model = new BlackBoxSheetModel($"{detail.Code} · black box", detail.BlackBoxText, detail.Events, detail.Environment);
        return Show(new BlackBoxView(model, CloseSheet), 620);
    }

    private void BlackBox_Click(object? sender, RoutedEventArgs e) => ShowBlackBox();

    /// <summary>The black box that came with one message of the conversation.</summary>
    public async Task<SheetHandle?> ShowBlackBoxAsync(BugEntryViewModel entry)
    {
        if (Detail is not { } detail) return null;
        try
        {
            if (await entry.LoadBlackBoxAsync() is not { } box) return null;
            return Show(new BlackBoxView(BlackBoxSheetModel.Of(box, $"{detail.Code} · {entry.Author} · {entry.When}"), CloseSheet), 620);
        }
        catch (Exception ex)
        {
            detail.Status = $"The black box cannot be read now: {ex.Message}";
            return null;
        }
    }

    // ---- pictures ----

    /// <summary>A picture big, in a sheet.</summary>
    public SheetHandle? ShowPicture(BugImageItem image) =>
        Host is null ? null : Show(new BugPictureView(image, Host, CloseSheet), 720);

    private async void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button || Detail is not { } detail) return;
        if (button.Classes.Contains("thumb") && button.DataContext is BugImageItem image)
        {
            e.Handled = true;
            ShowPicture(image);
        }
        else if (button.Classes.Contains("unpick") && button.DataContext is BugImageItem pending)
        {
            e.Handled = true;
            if (!detail.DetailImages.Remove(pending)) detail.AnswerImages.Remove(pending);
        }
        else if (button.Classes.Contains("entrybox") && button.DataContext is BugEntryViewModel entry)
        {
            e.Handled = true;
            await ShowBlackBoxAsync(entry);
        }
    }

    private async void DetailsPicture_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is { } detail) await PickInto(detail, detail.DetailImages, detail.AddDetailImage);
    }

    private async void AnswerPicture_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is { } detail) await PickInto(detail, detail.AnswerImages, detail.AddAnswerImage);
    }

    private async Task PickInto(BugDetailViewModel detail, ObservableCollection<BugImageItem> list, Action<BugImageDraft> add)
    {
        if (Host is null) return;
        try
        {
            foreach (var image in await Host.PickAsync(BugStore.MaxImages - list.Count)) add(image);
        }
        catch (Exception ex)
        {
            detail.Status = $"No picture added: {ex.Message}";
        }
    }

    /// <summary>The answer box, for the page to put the cursor in when a question waits.</summary>
    public void FocusAnswer()
    {
        if (Detail?.IsAsking == true && VisualRoot is not null) AnswerBox.Focus();
    }
}
