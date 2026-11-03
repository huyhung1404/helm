using Android.Content;
using Android.Views;
using Android.Widget;
using Helm.Ai.Auto;
using Helm.Core;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The morning brief or the evening review, to read again: a chip at the top of the card ("Bản tin sáng · 06:45 ▾")
/// when one came today or yesterday (<see cref="AiBriefChip"/>). A tap opens it (title and lines); "‹ Hôm qua" shows
/// yesterday's last one. Opening one marks it read.
/// </summary>
public partial class AskHelmActivity
{
    private AiBriefBook? _briefBook;
    private AiBriefChip? _chip;
    private bool _chipOpen;
    private bool _chipYesterday;
    private LinearLayout _chipBox = null!;
    private TextView _chipLabel = null!;
    private TextView _chipOther = null!;
    private TextView _chipTitle = null!;
    private TextView _chipText = null!;

    private View BuildBriefChip()
    {
        _chipBox = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone, Clickable = true };
        _chipBox.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        _chipBox.Background = Rounded(_colors.Bubble, 16);
        _chipBox.Click += (_, _) => ToggleChip();

        var line = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        line.SetGravity(GravityFlags.CenterVertical);
        _chipLabel = Text("", 14, _colors.Accent, bold: true);
        line.AddView(_chipLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _chipOther = Text("", 14, _colors.Accent);
        _chipOther.SetPadding(Dp(12), Dp(2), 0, Dp(2));
        _chipOther.Visibility = ViewStates.Gone;
        _chipOther.Click += (_, _) =>
        {
            _chipYesterday = !_chipYesterday;
            ShowChip();
        };
        line.AddView(_chipOther);
        _chipBox.AddView(line);

        _chipTitle = Text("", 15, _colors.Text, bold: true);
        _chipTitle.Visibility = ViewStates.Gone;
        _chipBox.AddView(_chipTitle, Margins(0, 8, 0, 0));
        _chipText = Text("", 14, _colors.Text);
        _chipText.SetLineSpacing(Dp(3), 1f);
        _chipText.Visibility = ViewStates.Gone;
        _chipBox.AddView(_chipText, Margins(0, 4, 0, 0));

        try
        {
            // A text selection is worked on as text: no brief there.
            if (Intent?.Action != Intent.ActionProcessText && HelmAndroidServices.Current.GetService<AiBriefBook>() is { } book)
            {
                _briefBook = book;
                _chip = AiBriefChip.Pick(book.Latest(20), book.Now, TimeZoneInfo.Local);
                ShowChip();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ask Helm: could not read the briefs");
        }
        return _chipBox;
    }

    private void ToggleChip()
    {
        _chipOpen = !_chipOpen;
        if (!_chipOpen) _chipYesterday = false;
        ShowChip();
    }

    private void ShowChip()
    {
        if (_chip is not { } chip || _briefBook is not { } book)
        {
            _chipBox.Visibility = ViewStates.Gone;
            return;
        }
        var shown = _chipYesterday && chip.Yesterday is { } before ? before : chip.Latest;
        _chipBox.Visibility = ViewStates.Visible;
        _chipLabel.Text = $"{AiBriefChip.Label(shown.Value, book.Now, TimeZoneInfo.Local)} {(_chipOpen ? "▴" : "▾")}";
        _chipLabel.ContentDescription = _chipOpen ? "Thu gọn bản tin" : "Mở bản tin";
        _chipOther.Visibility = _chipOpen && chip.Yesterday is not null ? ViewStates.Visible : ViewStates.Gone;
        _chipOther.Text = _chipYesterday ? "Hôm nay ›" : "‹ Hôm qua";
        _chipTitle.Visibility = _chipText.Visibility = _chipOpen ? ViewStates.Visible : ViewStates.Gone;
        if (!_chipOpen) return;
        _chipTitle.Text = shown.Value.Title;
        _chipText.Text = shown.Value.Text;
        MarkRead(book, shown);
    }

    private void MarkRead(AiBriefBook book, SyncedItem<AiBrief> brief)
    {
        if (brief.Value.ReadAt is not null) return;
        try
        {
            book.MarkRead([brief.Id]);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ask Helm: could not mark a brief read");
        }
    }
}
