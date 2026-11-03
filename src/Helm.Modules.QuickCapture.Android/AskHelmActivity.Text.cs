using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Text;
using Helm.Core.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Ask Helm's card on a text selected in another app (Android's text selection menu: "Helm: Dịch", "Helm: Sửa",
/// "Helm: Viết lại", "Helm"): the chosen action runs at once, or the user picks one or says what to do. The result can
/// replace the selection (when the app lets it), be copied or shared. Text only: no Helm tool is sent. "Helm: Kiểm tra
/// tin" is Ask Helm itself on the selection: it checks the post on the web (<see cref="WebSearch"/>).
/// </summary>
public partial class AskHelmActivity
{
    private const int ShownSelection = 280;

    private IAiText? _text;
    private string? _selection;
    private bool _writable;
    private TextActionResult? _result;

    /// <summary>The action an entry of the selection menu stands for; null: the user picks one ("Helm").</summary>
    protected virtual TextActionKind? SelectionAction => null;

    /// <summary>"Helm: Kiểm tra tin": the selection is a post for Ask Helm to check on the web, not a text to work on.</summary>
    protected virtual bool ChecksPost => false;

    /// <summary>Opened from a text selection: the card works on that text. False: Ask Helm as usual.</summary>
    private bool StartWithSelection(IServiceProvider services)
    {
        if (Intent?.Action != Intent.ActionProcessText) return false;
        if (ChecksPost) return CheckPost(Intent.GetCharSequenceExtra(Intent.ExtraProcessText)?.Trim() ?? "");
        _selection = Intent.GetCharSequenceExtra(Intent.ExtraProcessText)?.Trim() ?? "";
        // Only an app that asked for a result takes the text back, and only when it did not mark it read-only.
        _writable = CallingActivity is not null && !Intent.GetBooleanExtra(Intent.ExtraProcessTextReadonly, false);
        _text = services.GetRequiredService<IAiText>();
        _question.Text = Shorten(_selection);
        _question.Visibility = ViewStates.Visible;
        if (_selection.Length == 0)
        {
            ShowProblem("No text came with the selection.");
            return true;
        }
        if (_text.Unavailable(AiFeatureIds.TextActions) is { } problem)
        {
            ShowProblem(problem);
            return true;
        }
        if (SelectionAction is { } action) _ = RunTextAsync(action, null);
        else ChooseAction();
        return true;
    }

    /// <summary>The post goes to Ask Helm as "Kiểm tra tin này: …"; what the user says next is a follow-up, as in the card.</summary>
    private bool CheckPost(string post)
    {
        _question.Text = Shorten(post);
        _question.Visibility = ViewStates.Visible;
        if (post.Length == 0)
        {
            ShowProblem("No text came with the selection.");
            return true;
        }
        if (_assistant!.Unavailable() is { } problem)
        {
            ShowProblem(problem);
            return true;
        }
        _session = _assistant.Start([new PhoneTools(this)]);
        _heard.Text = "Kiểm tra tin này";
        _ = SendAsync(WebSearch.FactCheckRequest(post));
        return true;
    }

    /// <summary>"Helm" without an action: the common ones as buttons, the rest under "Khác", or the user says it.</summary>
    private void ChooseAction()
    {
        _heard.Text = "";
        _heard.Hint = "Anh muốn làm gì với đoạn này?";
        _heard.SetHintTextColor(_colors.Secondary);
        _status.Text = "Chọn, hoặc chạm micro rồi nói (\"trả lời là cảm ơn nhé\")";
        Buttons((TextActions.Label(TextActionKind.Translate), () => _ = RunTextAsync(TextActionKind.Translate, null), true),
            (TextActions.Label(TextActionKind.Reply), () => _ = RunTextAsync(TextActionKind.Reply, null), false),
            ("Khác ▾", MoreActions, false));
    }

    private void MoreActions()
    {
        var menu = new PopupMenu(this, _buttons);
        TextActionKind[] kinds = [TextActionKind.Translate, TextActionKind.Fix, TextActionKind.Rewrite, TextActionKind.Summarize, TextActionKind.Reply];
        for (var i = 0; i < kinds.Length; i++) menu.Menu!.Add(0, i, i, TextActions.Label(kinds[i]));
        // Copy and share stay at hand when the main button puts the result back.
        if (_result is not null && _writable)
        {
            menu.Menu!.Add(0, 100, 100, "Chép");
            menu.Menu!.Add(0, 101, 101, "Chia sẻ");
        }
        menu.MenuItemClick += (_, e) =>
        {
            switch (e.Item!.ItemId)
            {
                case 100: Copy(); break;
                case 101: Share(); break;
                default: _ = RunTextAsync(kinds[e.Item.ItemId], null); break;
            }
        };
        menu.Show();
    }

    /// <summary>Runs one action on the selection; <paramref name="instruction"/> is what the user said (a custom action or follow-up).</summary>
    private async Task RunTextAsync(TextActionKind kind, string? instruction)
    {
        if (_text is null || _selection is null || _busy) return;
        _recognizer?.Cancel();
        _listening = false;
        _busy = true;
        ClearResult();
        _question.Visibility = ViewStates.Visible;
        _heard.Text = instruction ?? TextActions.Label(kind);
        _status.Text = "Em đang làm…";
        _mic.Visibility = ViewStates.Invisible;
        AiTextAnswer answer;
        try
        {
            var request = TextActions.Request(kind, _selection, instruction, kind == TextActionKind.Custom ? _result?.Result : null);
            var text = _text;
            answer = await Task.Run(() => text.AskAsync(request, _cancel.Token));
        }
        catch (System.OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "A text action failed");
            answer = AiTextAnswer.Failed(ex.Message);
        }
        finally
        {
            _busy = false;
        }
        if (IsFinishing || IsDestroyed) return;
        _mic.Visibility = ViewStates.Visible;
        var result = answer.Ok ? TextActions.Parse(answer.Text) : null;
        if (result is null)
        {
            ShowProblem(answer.Problem ?? "Em chưa làm được: Anh thử lại nha.");
            _question.Visibility = ViewStates.Visible;
            return;
        }
        _result = result;
        ShowTextResult(result, answer.RanOn);
    }

    private void ShowTextResult(TextActionResult result, string? ranOn)
    {
        ClearResult();
        _question.Visibility = ViewStates.Visible;
        if (result.Note is { } note) _rows.AddView(Row(note, null), Margins(0, 8, 0, 0));
        _answer.Text = result.Result;
        _answer.SetTextIsSelectable(true);
        _answer.Visibility = ViewStates.Visible;
        _status.Text = ranOn is { } pc ? $"{pc} · miễn phí" : $"{Short(_assistant?.Current()?.Name ?? "")} qua API";
        Buttons(_writable ? ("Thay thế", Replace, true) : ("Chép", Copy, true),
            ("Chia sẻ", Share, false),
            ("Khác ▾", MoreActions, false));
    }

    /// <summary>Puts the result back in place of the selection, in the app it came from.</summary>
    private void Replace()
    {
        if (_result is null) return;
        SetResult(Result.Ok, new Intent().PutExtra(Intent.ExtraProcessText, _result.Result));
        Finish();
    }

    private void Copy()
    {
        if (_result is null) return;
        if (GetSystemService(ClipboardService) is ClipboardManager clipboard) clipboard.PrimaryClip = ClipData.NewPlainText("Helm", _result.Result);
        _status.Text = "Đã chép";
    }

    private void Share()
    {
        if (_result is null) return;
        var send = new Intent(Intent.ActionSend).SetType("text/plain").PutExtra(Intent.ExtraText, _result.Result);
        StartActivity(Intent.CreateChooser(send, "Chia sẻ"));
        Finish();
    }

    /// <summary>What the user said with a selection open: done to the selection (or to the last result: "ngắn hơn").</summary>
    private bool SendToSelection(string text)
    {
        if (_selection is null) return false;
        _ = RunTextAsync(TextActionKind.Custom, text);
        return true;
    }

    private static string Shorten(string text) => text.Length <= ShownSelection ? text : text[..ShownSelection].TrimEnd() + "…";
}

/// <summary>"Helm" in the text selection menu: the card asks what to do with the text.</summary>
[Activity(Name = QuickCaptureModule.AskHelmSelectionName, Label = "Helm", Exported = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ConfigurationChanges = AskHelmSelection.Changes,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class AskHelmSelectionActivity : AskHelmActivity
{
}

/// <summary>"Helm: Dịch": into Vietnamese, or Vietnamese into English.</summary>
[Activity(Name = QuickCaptureModule.AskHelmTranslateName, Label = "Helm: Dịch", Exported = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ConfigurationChanges = AskHelmSelection.Changes,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class AskHelmTranslateActivity : AskHelmActivity
{
    protected override TextActionKind? SelectionAction => TextActionKind.Translate;
}

/// <summary>"Helm: Sửa": spelling, grammar and punctuation, in the text's own language.</summary>
[Activity(Name = QuickCaptureModule.AskHelmFixName, Label = "Helm: Sửa", Exported = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ConfigurationChanges = AskHelmSelection.Changes,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class AskHelmFixActivity : AskHelmActivity
{
    protected override TextActionKind? SelectionAction => TextActionKind.Fix;
}

/// <summary>"Helm: Viết lại": politer and clearer.</summary>
[Activity(Name = QuickCaptureModule.AskHelmRewriteName, Label = "Helm: Viết lại", Exported = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ConfigurationChanges = AskHelmSelection.Changes,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class AskHelmRewriteActivity : AskHelmActivity
{
    protected override TextActionKind? SelectionAction => TextActionKind.Rewrite;
}

/// <summary>"Helm: Kiểm tra tin": is this post true? Ask Helm searches the web and names its sources (when Web search is on).</summary>
[Activity(Name = QuickCaptureModule.AskHelmCheckName, Label = "Helm: Kiểm tra tin", Exported = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ConfigurationChanges = AskHelmSelection.Changes,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class AskHelmCheckActivity : AskHelmActivity
{
    protected override bool ChecksPost => true;
}

internal static class AskHelmSelection
{
    /// <summary>Turning the phone or switching dark mode must not close the card and lose the result.</summary>
    public const ConfigChanges Changes = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden;
}
