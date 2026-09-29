using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Helm.Core;
using Helm.Core.Capture;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlertDialog = Android.App.AlertDialog;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The capture dialog over whatever app is open: "Save to Helm" in the share sheet (the shared text or link is
/// filled in) and the Quick Settings tile (empty). A row of targets (Note, Task, Debt), the text, and a live line that
/// says what Save will do. Native Android views, not Avalonia: it opens instantly without Helm's main screen.
/// </summary>
[Activity(Name = QuickCaptureModule.ActivityName, Label = "Save to Helm", Exported = true, ExcludeFromRecents = true, NoHistory = true,
    TaskAffinity = "", Theme = "@android:style/Theme.Translucent.NoTitleBar",
    WindowSoftInputMode = SoftInput.StateAlwaysVisible | SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class QuickCaptureActivity : Activity
{
    private IReadOnlyList<ICaptureTarget> _targets = [];
    private ICaptureTarget? _selected;
    private EditText? _input;
    private TextView? _preview;
    private RadioGroup? _group;
    private AlertDialog? _dialog;
    private ILogger? _logger;
    private DialogColors _colors = DialogColors.Light;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            var services = HelmAndroidServices.Current;
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger<QuickCaptureActivity>();
            var settings = services.GetRequiredService<ISettingsStoreFactory>();
            _colors = IsDark(settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.Theme) ? DialogColors.Dark : DialogColors.Light;
            _targets =QuickCaptureModule.AvailableTargets(services.GetServices<ICaptureTarget>(), settings);
            if (_targets.Count == 0)
            {
                Toast.MakeText(this, "Turn on Notes or Tracker in Helm to save into them.", ToastLength.Long)?.Show();
                Finish();
                return;
            }
            var wanted = settings.Get<QuickCaptureSettings>(QuickCaptureModule.ModuleId).Current.DefaultTarget;
            _selected = _targets.FirstOrDefault(t => t.Id == wanted) ?? _targets[0];
            ShowDialog(SharedText(Intent));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not open Quick Capture");
            Toast.MakeText(this, $"Could not open Quick Capture: {ex.Message}", ToastLength.Long)?.Show();
            Finish();
        }
    }

    /// <summary>The shared text; a subject (e.g. a page title shared with its link) goes first, as the note's title.</summary>
    private static string SharedText(Intent? intent)
    {
        if (intent?.Action != Intent.ActionSend) return "";
        var text = intent.GetStringExtra(Intent.ExtraText)?.Trim() ?? "";
        var subject = intent.GetStringExtra(Intent.ExtraSubject)?.Trim() ?? "";
        return subject.Length > 0 && !text.Contains(subject, StringComparison.Ordinal) ? $"{subject}\n{text}".Trim() : text;
    }

    private void ShowDialog(string text)
    {
        var pad = Dp(20);
        // Every view is made from the dialog's own theme: views made from this activity (a translucent, old-style
        // theme) get text colours that do not match the dialog's background.
        var ui = new ContextThemeWrapper(this, _colors.DialogTheme);
        var layout = new LinearLayout(ui) { Orientation = Orientation.Vertical };
        layout.SetPadding(pad, Dp(4), pad, 0);

        // The targets, one tap each (a "/t " prefix in the text also picks one).
        _group = new RadioGroup(ui) { Orientation = Orientation.Horizontal };
        foreach (var target in _targets)
        {
            var button = new RadioButton(ui) { Text = target.Name, Id = View.GenerateViewId(), Tag = target.Id };
            button.SetTextColor(_colors.Text);
            button.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(_colors.Accent);
            button.SetPadding(Dp(4), 0, Dp(16), 0);
            _group.AddView(button);
            if (target == _selected) button.Checked = true;
        }
        _group.CheckedChange += (_, e) =>
        {
            var id = _group.FindViewById<RadioButton>(e.CheckedId)?.Tag?.ToString();
            _selected = _targets.FirstOrDefault(t => t.Id == id) ?? _selected;
            Update();
        };
        layout.AddView(_group);

        _input = new EditText(ui)
        {
            Text = text,
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            Gravity = GravityFlags.Top | GravityFlags.Start,
        };
        _input.SetTextColor(_colors.Text);
        _input.SetHintTextColor(_colors.Secondary);
        _input.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(_colors.Accent);
        _input.SetMinLines(2);
        _input.SetMaxLines(8);
        _input.TextChanged += (_, _) => Update();
        layout.AddView(_input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _preview = new TextView(ui) { TextSize = 13 };
        _preview.SetPadding(Dp(4), Dp(4), Dp(4), 0);
        layout.AddView(_preview);

        var scroll = new ScrollView(ui);
        scroll.AddView(layout);

        var title = new TextView(ui) { Text = "Quick Capture", TextSize = 20 };
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        title.SetTextColor(_colors.Text);
        title.SetPadding(pad, Dp(18), pad, Dp(6));

        _dialog = new AlertDialog.Builder(ui, _colors.DialogTheme)
            .SetCustomTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton("Save", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancel", (_, _) => Finish())!
            .SetOnCancelListener(new Listener(Finish))!
            .Create()!;
        // Helm's card: its colour in light and dark, rounded like the app's cards.
        var card = new Android.Graphics.Drawables.GradientDrawable();
        card.SetColor(_colors.Background);
        card.SetCornerRadius(Dp(12));
        _dialog.Window?.SetBackgroundDrawable(new Android.Graphics.Drawables.InsetDrawable(card, Dp(16)));
        _dialog.ShowEvent += (_, _) =>
        {
            foreach (var kind in new[] { DialogButtonType.Positive, DialogButtonType.Negative })
                _dialog.GetButton((int)kind)?.SetTextColor(new Android.Content.Res.ColorStateList(
                    [[-Android.Resource.Attribute.StateEnabled], []], [_colors.Disabled, _colors.Accent]));
            // Save keeps the dialog open when it cannot save, so nothing typed is lost.
            _dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) => Save();
            Update();
        };
        _dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible | SoftInput.AdjustResize);
        _dialog.Show();
        _input.RequestFocus();
        _input.SetSelection(_input.Text?.Length ?? 0);
    }

    private (ICaptureTarget? Target, string Text) Resolve() => CaptureRouter.Resolve(_targets, _selected, _input?.Text ?? "");

    private void Update()
    {
        if (_preview is null) return;
        var (target, text) = Resolve();
        CapturePreview preview;
        try
        {
            preview = target is null ? new CapturePreview(false, "") : text.Trim().Length == 0 ? new CapturePreview(false, target.Example) : target.Preview(text);
        }
        catch (Exception ex)
        {
            preview = new CapturePreview(false, ex.Message);
        }
        _preview.Text = preview.Text;
        _preview.SetTextColor(_colors.Secondary);
        if (_dialog?.GetButton((int)DialogButtonType.Positive) is { } save) save.Enabled = preview.CanSave;
        // A "/t" prefix picks its target: show it on the buttons.
        if (target is not null && _group is not null)
        {
            for (var i = 0; i < _group.ChildCount; i++)
                if (_group.GetChildAt(i) is RadioButton b && b.Tag?.ToString() == target.Id && !b.Checked) b.Checked = true;
        }
    }

    private void Save()
    {
        var (target, text) = Resolve();
        if (target is null) return;
        CaptureResult result;
        try
        {
            result = target.Capture(text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Quick Capture failed");
            result = new CaptureResult(false, $"Could not save: {ex.Message}");
        }
        if (!result.Saved)
        {
            if (_preview is not null)
            {
                _preview.Text = result.Message;
                _preview.SetTextColor(_colors.Danger);
            }
            return;
        }
        Toast.MakeText(this, result.Message, ToastLength.Short)?.Show();
        _dialog?.Dismiss();
        Finish();
    }

    /// <summary>Helm's own theme choice (General → Theme); System follows the phone's dark theme.</summary>
    private bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => ((Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo) & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes,
    };

    /// <summary>The dialog in Helm's colours (the same as App.axaml and the Fluent accent of the Android app).</summary>
    private sealed record DialogColors(int DialogTheme, Color Background, Color Text, Color Secondary, Color Accent, Color Disabled, Color Danger)
    {
        public static readonly DialogColors Light = new(Android.Resource.Style.ThemeMaterialLightDialogAlert,
            Color.ParseColor("#FBFBFB"), Color.ParseColor("#1A1A1A"), Color.ParseColor("#5F5F5F"), Color.ParseColor("#0067C0"),
            Color.ParseColor("#A0A0A0"), Color.ParseColor("#C42B1C"));

        public static readonly DialogColors Dark = new(Android.Resource.Style.ThemeMaterialDialogAlert,
            Color.ParseColor("#2B2B2B"), Color.ParseColor("#FFFFFF"), Color.ParseColor("#C5C5C5"), Color.ParseColor("#4CC2FF"),
            Color.ParseColor("#6E6E6E"), Color.ParseColor("#FF99A4"));
    }

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    private sealed class Listener(Action action) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => action();
    }
}
