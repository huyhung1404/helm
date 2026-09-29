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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            var services = HelmAndroidServices.Current;
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger<QuickCaptureActivity>();
            var settings = services.GetRequiredService<ISettingsStoreFactory>();
            _targets = QuickCaptureModule.AvailableTargets(services.GetServices<ICaptureTarget>(), settings);
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
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(pad, Dp(4), pad, 0);

        // The targets, one tap each (a "/t " prefix in the text also picks one).
        _group = new RadioGroup(this) { Orientation = Orientation.Horizontal };
        foreach (var target in _targets)
        {
            var button = new RadioButton(this) { Text = target.Name, Id = View.GenerateViewId(), Tag = target.Id };
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

        _input = new EditText(this)
        {
            Text = text,
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            Gravity = GravityFlags.Top | GravityFlags.Start,
        };
        _input.SetMinLines(2);
        _input.SetMaxLines(8);
        _input.TextChanged += (_, _) => Update();
        layout.AddView(_input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _preview = new TextView(this) { TextSize = 13 };
        _preview.SetPadding(Dp(4), Dp(4), Dp(4), 0);
        layout.AddView(_preview);

        var scroll = new ScrollView(this);
        scroll.AddView(layout);

        _dialog = new AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)
            .SetTitle("Quick Capture")!
            .SetView(scroll)!
            .SetPositiveButton("Save", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancel", (_, _) => Finish())!
            .SetOnCancelListener(new Listener(Finish))!
            .Create()!;
        _dialog.ShowEvent += (_, _) =>
        {
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
        _preview.Alpha = preview.CanSave ? 0.9f : 0.6f;
        _preview.SetTextColor(_input?.CurrentTextColor is { } c ? new Color(c) : Color.Gray);
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
                _preview.SetTextColor(Color.ParseColor("#C42B1C"));
                _preview.Alpha = 1f;
            }
            return;
        }
        Toast.MakeText(this, result.Message, ToastLength.Short)?.Show();
        _dialog?.Dismiss();
        Finish();
    }

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    private sealed class Listener(Action action) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => action();
    }
}
