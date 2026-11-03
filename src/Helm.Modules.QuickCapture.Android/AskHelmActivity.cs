using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Speech;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Helm.Ai.Assistant;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Ask Helm's card over whatever app is open, opened by its Quick Settings tile: it listens at once and shows the words
/// as they come, sends them to the model picked in its header, then lists what it saved (each with Undo) or answers.
/// Only a change Helm cannot take back waits for a yes. Native Android views, like Quick Capture: no Helm screen first.
/// </summary>
[Activity(Name = QuickCaptureModule.AskHelmActivityName, Label = "Ask Helm", Exported = AskHelmBuild.Exported, ExcludeFromRecents = true,
    TaskAffinity = "", Theme = "@android:style/Theme.Translucent.NoTitleBar", LaunchMode = LaunchMode.SingleTask,
    // Turning the phone or switching dark mode must not close the card mid-turn (and lose its Undo list).
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
public sealed class AskHelmActivity : Activity, IRecognitionListener
{
    private const int MicPermissionRequest = 41;
    private const int AutoCloseMs = 7000;

    private HelmAssistant? _assistant;
    private AssistantSession? _session;
    private SpeechRecognizer? _recognizer;
    private CancellationTokenSource _cancel = new();
    private TaskCompletionSource<bool>? _confirm;
    private ILogger? _logger;
    private AskColors _colors = AskColors.Dark;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private Java.Lang.Runnable? _autoClose;
    private bool _listening;
    private bool _busy;

    private TextView _model = null!;
    private TextView _question = null!;
    private TextView _heard = null!;
    private TextView _status = null!;
    private ImageView _mic = null!;
    private LinearLayout _typing = null!;
    private EditText _input = null!;
    private LinearLayout _rows = null!;
    private TextView _answer = null!;
    private LinearLayout _buttons = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            var services = HelmAndroidServices.Current;
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger<AskHelmActivity>();
            var settings = services.GetRequiredService<ISettingsStoreFactory>();
            _colors = IsDark(settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.Theme) ? AskColors.Dark : AskColors.Light;
            _assistant = services.GetRequiredService<HelmAssistant>();
            SetContentView(Build());
            ShowModel();
            if (_assistant.Unavailable() is { } problem)
            {
                ShowProblem(problem);
                return;
            }
            _session = _assistant.Start([new PhoneTools(this)]);
            Listen();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not open Ask Helm");
            Toast.MakeText(this, $"Could not open Ask Helm: {ex.Message}", ToastLength.Long)?.Show();
            Finish();
        }
    }

    protected override void OnDestroy()
    {
        // Closing the card withdraws the turn (a PC that took it stops), and a question waiting for a yes is a no.
        _cancel.Cancel();
        _confirm?.TrySetResult(false);
        StopAutoClose();
        _session?.End();
        _recognizer?.Destroy();
        _recognizer = null;
        base.OnDestroy();
    }

    // ---- the card --------------------------------------------------------------------------------------------

    private View Build()
    {
        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Argb(150, 8, 10, 12));
        // A tap outside the card closes it, as anywhere in Android.
        root.Click += (_, _) => Finish();

        var card = new LinearLayout(this) { Orientation = Orientation.Vertical, Clickable = true };
        card.SetPadding(Dp(20), Dp(16), Dp(20), Dp(24));
        var shape = new GradientDrawable();
        shape.SetColor(_colors.Card);
        shape.SetCornerRadii([Dp(26), Dp(26), Dp(26), Dp(26), 0, 0, 0, 0]);
        card.Background = shape;
        card.Touch += (_, e) => { StopAutoClose(); e.Handled = false; };

        // Header: the name, the model to ask, close.
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var title = Text("Ask Helm", 17, _colors.Text, bold: true);
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _model = Pill("", () => PickModel());
        _model.ContentDescription = "Choose the model";
        header.AddView(_model);
        var close = Text("✕", 20, _colors.Secondary);
        close.SetPadding(Dp(14), Dp(6), Dp(4), Dp(6));
        close.ContentDescription = "Close";
        close.Click += (_, _) => Finish();
        header.AddView(close);
        card.AddView(header);

        _question = Text("", 15, _colors.Text);
        _question.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        _question.Background = Rounded(_colors.Bubble, 16);
        _question.Visibility = ViewStates.Gone;
        card.AddView(_question, Margins(0, 14, 0, 0));

        _heard = Text("", 22, _colors.Text);
        _heard.SetMinHeight(Dp(64));
        card.AddView(_heard, Margins(0, 14, 0, 0));

        _rows = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.AddView(_rows, Margins(0, 4, 0, 0));

        _answer = Text("", 16, _colors.Text);
        _answer.Visibility = ViewStates.Gone;
        card.AddView(_answer, Margins(0, 8, 0, 0));

        _status = Text("", 13, _colors.Secondary);
        _status.Gravity = GravityFlags.CenterHorizontal;
        card.AddView(_status, Margins(0, 10, 0, 0));

        _mic = new ImageView(this);
        _mic.SetImageResource(Resource.Drawable.ask_helm_tile);
        _mic.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(_colors.OnAccent);
        _mic.SetScaleType(ImageView.ScaleType.Center);
        _mic.SetPadding(Dp(18), Dp(18), Dp(18), Dp(18));
        _mic.Background = Rounded(_colors.Accent, 40);
        _mic.ContentDescription = "Speak";
        _mic.Click += (_, _) => { if (_listening) _recognizer?.StopListening(); else Listen(); };
        var micRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        micRow.SetGravity(GravityFlags.Center);
        micRow.AddView(_mic, new LinearLayout.LayoutParams(Dp(76), Dp(76)));
        var keyboard = Text("Aa", 15, _colors.Secondary, bold: true);
        keyboard.Gravity = GravityFlags.Center;
        keyboard.Background = Outline(_colors.Line, 24);
        keyboard.ContentDescription = "Type instead";
        keyboard.Click += (_, _) => ShowTyping(null);
        micRow.AddView(keyboard, new LinearLayout.LayoutParams(Dp(48), Dp(48)) { LeftMargin = Dp(20) });
        card.AddView(micRow, Margins(0, 12, 0, 0));

        _typing = new LinearLayout(this) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        _typing.SetGravity(GravityFlags.CenterVertical);
        _input = new EditText(this) { Hint = "Gõ điều Anh muốn…", ImeOptions = ImeAction.Send };
        _input.SetSingleLine(true);
        _input.SetTextColor(_colors.Text);
        _input.SetHintTextColor(_colors.Secondary);
        // The card's own field, not the system's (white with an orange focus line on some phones).
        _input.Background = Rounded(_colors.Row, 16);
        _input.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        _input.EditorAction += (_, e) => { if (e.ActionId == ImeAction.Send) { SendTyped(); e.Handled = true; } };
        _typing.AddView(_input, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var send = Pill("Gửi", SendTyped, primary: true);
        _typing.AddView(send, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
        card.AddView(_typing, Margins(0, 12, 0, 0));

        _buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        card.AddView(_buttons, Margins(0, 16, 0, 0));

        root.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        return root;
    }

    private void ShowModel()
    {
        var current = _assistant?.Current();
        _model.Text = current is null ? "No model ▾" : $"{Short(current.Name)} ▾";
    }

    private void PickModel()
    {
        if (_assistant is null || _busy) return;
        var options = _assistant.Models();
        if (options.Count == 0)
        {
            Toast.MakeText(this, "No model yet: add a Claude or Grok key on the PC (AI → Models & keys), or an agent (AI → Proxy).", ToastLength.Long)?.Show();
            return;
        }
        var menu = new PopupMenu(this, _model);
        for (var i = 0; i < options.Count; i++) menu.Menu!.Add(0, i, i, options[i].Name);
        menu.MenuItemClick += (_, e) =>
        {
            _assistant.Choose(options[e.Item!.ItemId]);
            // Another model is another conversation.
            _session?.End();
            _session = _assistant.Start([new PhoneTools(this)]);
            ShowModel();
        };
        menu.Show();
    }

    // ---- listening ------------------------------------------------------------------------------------------

    private void Listen()
    {
        StopAutoClose();
        if (_busy) return;
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.RecordAudio], MicPermissionRequest);
            return;
        }
        if (!SpeechRecognizer.IsRecognitionAvailable(this))
        {
            ShowTyping("This phone has no speech recognition: type instead.");
            return;
        }
        _recognizer ??= SpeechRecognizer.CreateSpeechRecognizer(this);
        _recognizer!.SetRecognitionListener(this);
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech)
            .PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm)
            .PutExtra(RecognizerIntent.ExtraLanguage, "vi-VN")
            .PutExtra(RecognizerIntent.ExtraPartialResults, true);
        ClearResult();
        _heard.Text = "";
        _heard.Hint = "Anh cứ nói…";
        _heard.SetHintTextColor(_colors.Secondary);
        _status.Text = "Đang nghe · nói xong là tự gửi";
        _mic.Alpha = 1f;
        _listening = true;
        _recognizer.StartListening(intent);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != MicPermissionRequest) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) Listen();
        else ShowTyping("Helm may not use the microphone: type instead, or allow it in the phone's settings.");
    }

    public void OnReadyForSpeech(Bundle? @params) { }
    public void OnBeginningOfSpeech() { }
    public void OnRmsChanged(float rmsdB) => _mic.Alpha = 0.7f + Math.Clamp(rmsdB / 30f, 0f, 0.3f);
    public void OnBufferReceived(byte[]? buffer) { }
    public void OnEndOfSpeech() => _status.Text = "…";
    public void OnEvent(int eventType, Bundle? @params) { }

    public void OnPartialResults(Bundle? partialResults)
    {
        if (partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition) is { Count: > 0 } words) _heard.Text = words[0];
    }

    public void OnResults(Bundle? results)
    {
        _listening = false;
        var text = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition) is { Count: > 0 } words ? words[0] : "";
        if (text.Trim().Length == 0)
        {
            _status.Text = "Em chưa nghe rõ: chạm micro để nói lại.";
            return;
        }
        _heard.Text = text;
        _ = SendAsync(text);
    }

    public void OnError(SpeechRecognizerError error)
    {
        _listening = false;
        _mic.Alpha = 1f;
        _status.Text = error switch
        {
            SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout => "Em chưa nghe rõ: chạm micro để nói lại.",
            SpeechRecognizerError.Network or SpeechRecognizerError.NetworkTimeout => "Nhận giọng nói cần mạng: kiểm tra mạng, hoặc gõ.",
            SpeechRecognizerError.InsufficientPermissions => "Helm chưa được dùng micro: gõ, hoặc cho phép trong cài đặt.",
            _ => $"Nhận giọng nói lỗi ({error}): chạm micro để thử lại, hoặc gõ.",
        };
    }

    private void ShowTyping(string? why)
    {
        _recognizer?.Cancel();
        _listening = false;
        _typing.Visibility = ViewStates.Visible;
        if (why is not null) _status.Text = why;
        _input.RequestFocus();
        ((InputMethodManager?)GetSystemService(InputMethodService))?.ShowSoftInput(_input, ShowFlags.Implicit);
    }

    private void SendTyped()
    {
        var text = _input.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        _input.Text = "";
        _heard.Text = text;
        ((InputMethodManager?)GetSystemService(InputMethodService))?.HideSoftInputFromWindow(_input.WindowToken, HideSoftInputFlags.None);
        _ = SendAsync(text);
    }

    // ---- asking ---------------------------------------------------------------------------------------------

    private async Task SendAsync(string text)
    {
        if (_session is null || _busy) return;
        _busy = true;
        ClearResult();
        _status.Text = $"{Short(_session.Model?.Name ?? "")} đang hiểu câu của Anh…";
        _mic.Visibility = ViewStates.Invisible;
        AssistantReply reply;
        try
        {
            var session = _session;
            reply = await Task.Run(() => session.SendAsync(text, ConfirmAsync, _cancel.Token));
        }
        catch (System.OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ask Helm failed");
            reply = new AssistantReply("", [], false, ex.Message);
        }
        finally
        {
            _busy = false;
        }
        if (IsFinishing || IsDestroyed) return;
        _mic.Visibility = ViewStates.Visible;
        if (reply.Problem is { } problem) ShowProblem(problem, reply.Done);
        else ShowReply(reply);
    }

    /// <summary>Shown for the calls that wait for a yes; the assistant's thread waits for the answer.</summary>
    private Task<bool> ConfirmAsync(IReadOnlyList<PendingAction> pending)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirm = answer;
        RunOnUiThread(() =>
        {
            ClearResult();
            _status.Text = "Việc này sửa dữ liệu cũ: Anh xác nhận nhé";
            foreach (var p in pending) _rows.AddView(Row(p.Summary, null), Margins(0, 8, 0, 0));
            Buttons(("Xác nhận", () => { ClearButtons(); _status.Text = "…"; answer.TrySetResult(true); }, true),
                ("Huỷ", () => { ClearButtons(); answer.TrySetResult(false); }, false));
        });
        return answer.Task;
    }

    private void ShowReply(AssistantReply reply)
    {
        ClearResult();
        foreach (var action in reply.Done) _rows.AddView(Row(action.Summary, action), Margins(0, 8, 0, 0));
        var asks = reply.Done.Count == 0 && reply.Text.TrimEnd().EndsWith('?');
        if (asks)
        {
            // A question back: show it and listen for the answer at once.
            _question.Text = reply.Text;
            _question.Visibility = ViewStates.Visible;
            Listen();
            return;
        }
        if (reply.Text.Length > 0)
        {
            _answer.Text = reply.Text;
            _answer.Visibility = ViewStates.Visible;
        }
        // Where it ran: Claude Code on a Helm PC (free) or the API (counted in the AI page's costs).
        var ranOn = reply.RanOn is { } pc ? $"{pc} · miễn phí" : $"{Short(_session?.Model?.Name ?? "")} qua API";
        _status.Text = reply.Done.Count > 0 ? $"Đã lưu · {ranOn} · tự đóng sau vài giây" : ranOn;
        Buttons(("Nói tiếp", Listen, true), ("Xong", Finish, false));
        if (reply.Done.Count > 0) StartAutoClose();
    }

    private void ShowProblem(string problem, IReadOnlyList<AssistantAction>? done = null)
    {
        ClearResult();
        foreach (var action in done ?? []) _rows.AddView(Row(action.Summary, action), Margins(0, 8, 0, 0));
        _answer.Text = problem;
        _answer.SetTextColor(_colors.Danger);
        _answer.Visibility = ViewStates.Visible;
        _status.Text = "";
        Buttons(("Thử lại", Listen, true), ("Đóng", Finish, false));
    }

    /// <summary>One thing done (with Undo while it can be taken back) or one thing waiting for a yes.</summary>
    private View Row(string summary, AssistantAction? action)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(14), Dp(12), Dp(10), Dp(12));
        row.Background = Rounded(_colors.Row, 16);
        var text = Text(summary, 15, _colors.Text);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        if (action is { CanUndo: true })
        {
            TextView? undo = null;
            undo = Pill("Hoàn tác", async () =>
            {
                StopAutoClose();
                undo!.Enabled = false;
                try
                {
                    await Task.Run(() => action.UndoAsync(_cancel.Token));
                    text.PaintFlags |= PaintFlags.StrikeThruText;
                    text.Alpha = 0.6f;
                    undo.Text = "Đã hoàn tác";
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Undo failed");
                    undo.Enabled = true;
                    undo.Text = "Hoàn tác lỗi";
                }
            });
            row.AddView(undo);
        }
        return row;
    }

    private void ClearResult()
    {
        _rows.RemoveAllViews();
        _answer.Visibility = ViewStates.Gone;
        _answer.SetTextColor(_colors.Text);
        _question.Visibility = ViewStates.Gone;
        ClearButtons();
    }

    private void ClearButtons()
    {
        _buttons.RemoveAllViews();
        _buttons.Visibility = ViewStates.Gone;
    }

    private void Buttons(params (string Text, Action Click, bool Primary)[] buttons)
    {
        ClearButtons();
        foreach (var (label, click, primary) in buttons)
        {
            var b = Pill(label, click, primary);
            b.Gravity = GravityFlags.Center;
            b.SetMinHeight(Dp(48));
            _buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { LeftMargin = _buttons.ChildCount > 0 ? Dp(10) : 0 });
        }
        _buttons.Visibility = ViewStates.Visible;
    }

    private void StartAutoClose()
    {
        StopAutoClose();
        _autoClose = new Java.Lang.Runnable(Finish);
        _handler.PostDelayed(_autoClose, AutoCloseMs);
    }

    private void StopAutoClose()
    {
        if (_autoClose is null) return;
        _handler.RemoveCallbacks(_autoClose);
        _autoClose = null;
    }

    // ---- small views ----------------------------------------------------------------------------------------

    private TextView Text(string text, float sp, Color color, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = sp };
        view.SetTextColor(color);
        if (bold) view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        return view;
    }

    private TextView Pill(string text, Action click, bool primary = false)
    {
        var view = Text(text, 14, primary ? _colors.OnAccent : _colors.Text, bold: true);
        view.SetPadding(Dp(14), Dp(8), Dp(14), Dp(8));
        view.Background = primary ? Rounded(_colors.Accent, 22) : Outline(_colors.Line, 22);
        view.Click += (_, _) => click();
        return view;
    }

    private GradientDrawable Rounded(Color color, int radiusDp)
    {
        var d = new GradientDrawable();
        d.SetColor(color);
        d.SetCornerRadius(Dp(radiusDp));
        return d;
    }

    private GradientDrawable Outline(Color color, int radiusDp)
    {
        var d = new GradientDrawable();
        d.SetColor(Color.Transparent);
        d.SetStroke(Dp(1), color);
        d.SetCornerRadius(Dp(radiusDp));
        return d;
    }

    private LinearLayout.LayoutParams Margins(int left, int top, int right, int bottom)
    {
        var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        p.SetMargins(Dp(left), Dp(top), Dp(right), Dp(bottom));
        return p;
    }

    private static string Short(string name) => name.StartsWith("Claude ", StringComparison.Ordinal) ? name[7..] : name;

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    private bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => ((Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo) & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes,
    };

    /// <summary>The card in Helm's colours (the Fluent accent of the Android app), light and dark.</summary>
    private sealed record AskColors(Color Card, Color Text, Color Secondary, Color Accent, Color OnAccent, Color Line, Color Row, Color Bubble, Color Danger)
    {
        public static readonly AskColors Light = new(Color.ParseColor("#FBFBFB"), Color.ParseColor("#1A1A1A"), Color.ParseColor("#5F5F5F"),
            Color.ParseColor("#0067C0"), Color.White, Color.ParseColor("#D5D9DE"), Color.ParseColor("#EEF1F4"), Color.ParseColor("#E2EEF8"),
            Color.ParseColor("#C42B1C"));

        public static readonly AskColors Dark = new(Color.ParseColor("#202428"), Color.White, Color.ParseColor("#C5C9CE"),
            Color.ParseColor("#4CC2FF"), Color.ParseColor("#0B2233"), Color.ParseColor("#3A4048"), Color.ParseColor("#2A2F35"),
            Color.ParseColor("#2C3A47"), Color.ParseColor("#FF99A4"));
    }
}

/// <summary>Debug builds let adb open the card (`am start`) for testing; release builds only open it from the tile.</summary>
internal static class AskHelmBuild
{
#if DEBUG
    public const bool Exported = true;
#else
    public const bool Exported = false;
#endif
}
