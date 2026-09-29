using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Service.Autofill;
using Android.Text;
using Android.Views;
using Android.Views.Autofill;
using Android.Widget;
using AndroidX.AppCompat.App;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;
using View = Android.Views.View;

namespace Helm.Modules.Vault.Autofill;

/// <summary>
/// What autofill opens over the other app: unlock the vault (fingerprint or face first when it is set up, else the
/// password), then fill with the logins for the site or app, or let the user pick one. For an app, the picked login
/// remembers it ("androidapp://package") so next time it is offered at once; for a site only when the user ticks it.
/// Screenshots of it are blocked, like the vault in Helm.
/// </summary>
[Activity(Name = "com.huyhung1404.helm.vault.AutofillActivity", Label = "Helm Vault", Exported = false, ExcludeFromRecents = true, NoHistory = true, TaskAffinity = "",
    Theme = "@style/Theme.AppCompat.DayNight.Dialog.Alert", WindowSoftInputMode = SoftInput.StateAlwaysVisible | SoftInput.AdjustResize)]
public sealed class AutofillActivity : AppCompatActivity
{
    public enum Mode
    {
        /// <summary>The vault was locked: answer with a whole FillResponse.</summary>
        Unlock,
        /// <summary>"Choose from Helm Vault…": answer with one Dataset.</summary>
        Pick,
    }

    private const string ExtraMode = "helm.autofill.mode";
    private const string ExtraUsername = "helm.autofill.username";
    private const string ExtraPassword = "helm.autofill.password";
    private const string ExtraCode = "helm.autofill.code";
    private const string ExtraHost = "helm.autofill.host";
    private const string ExtraApp = "helm.autofill.app";
    private const string ExtraPackage = "helm.autofill.package";

    private AutofillForm _form = null!;
    private Mode _mode;
    private VaultSession _session = null!;
    private VaultStore _store = null!;
    private ILogger? _logger;
    private AlertDialog? _dialog;
    private bool _answered;

    public static Intent Create(Context context, AutofillForm form, Mode mode)
    {
        var intent = new Intent(context, typeof(AutofillActivity));
        intent.PutExtra(ExtraMode, (int)mode);
        if (form.Username is { } u) intent.PutExtra(ExtraUsername, u);
        if (form.Password is { } p) intent.PutExtra(ExtraPassword, p);
        if (form.Code is { } c) intent.PutExtra(ExtraCode, c);
        intent.PutExtra(ExtraHost, form.Target.Host);
        intent.PutExtra(ExtraApp, form.Target.AppId);
        intent.PutExtra(ExtraPackage, form.PackageName);
        return intent;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Never in screenshots, recents thumbnails or screen sharing.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        ActivityHost.OnCreated(this);
        try
        {
            var services = HelmAndroidServices.Current;
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger<AutofillActivity>();
            _session = services.GetRequiredService<VaultSession>();
            _store = services.GetRequiredService<VaultStore>();
            _mode = (Mode)(Intent?.GetIntExtra(ExtraMode, 0) ?? 0);
            _form = new AutofillForm(Id(ExtraUsername), Id(ExtraPassword), Id(ExtraCode),
                new AutofillTarget(Intent?.GetStringExtra(ExtraHost), Intent?.GetStringExtra(ExtraApp)), Intent?.GetStringExtra(ExtraPackage) ?? "");
            if (_session.State == VaultState.Unlocked) AfterUnlock();
            else if (_session.State == VaultState.NotSetUp) Fail("Set up the Vault in Helm first.");
            else ShowUnlock();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not open autofill");
            Fail($"Could not open Helm Vault: {ex.Message}");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ActivityHost.OnResumed(this);
    }

    protected override void OnPause()
    {
        ActivityHost.OnPaused(this);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _dialog?.Dismiss();
        base.OnDestroy();
    }

    private AutofillId? Id(string extra) =>
        OperatingSystem.IsAndroidVersionAtLeast(33) ? Intent?.GetParcelableExtra(extra, Java.Lang.Class.FromType(typeof(AutofillId))) as AutofillId
#pragma warning disable CA1422
            : Intent?.GetParcelableExtra(extra) as AutofillId;
#pragma warning restore CA1422

    // ---- Unlock -----------------------------------------------------------------------------------------------

    private void ShowUnlock()
    {
        var layout = Column();
        layout.AddView(Text(_form.Target.Host is { } host ? $"Sign in to {host}" : "Fill this sign-in", secondary: true));
        var password = new EditText(this)
        {
            Hint = "Vault password",
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
            ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants,
        };
        layout.AddView(password, Wide());
        var error = Text("", secondary: true);
        error.SetTextColor(Color.ParseColor("#C42B1C"));
        error.Visibility = ViewStates.Gone;
        layout.AddView(error);

        var builder = new AlertDialog.Builder(this)
            .SetTitle("Unlock Helm Vault")!
            .SetView(layout)!
            .SetPositiveButton("Unlock", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancel", (_, _) => Cancel())!
            .SetOnCancelListener(new Listener(Cancel))!;
        if (_session.CanUseDeviceUnlock) builder.SetNeutralButton(_session.DeviceUnlockName, (IDialogInterfaceOnClickListener?)null);
        _dialog = builder.Create()!;
        _dialog.ShowEvent += (_, _) =>
        {
            _dialog.GetButton((int)DialogButtonType.Positive)!.Click += async (_, _) => await UnlockWithPasswordAsync(password, error);
            if (_session.CanUseDeviceUnlock)
            {
                _dialog.GetButton((int)DialogButtonType.Neutral)!.Click += async (_, _) => await UnlockWithDeviceAsync(error);
                // Fingerprint or face first: most of the time that is all it takes.
                password.Post(async () => await UnlockWithDeviceAsync(error));
            }
        };
        password.EditorAction += async (_, e) =>
        {
            e.Handled = true;
            await UnlockWithPasswordAsync(password, error);
        };
        _dialog.Show();
    }

    private async Task UnlockWithPasswordAsync(EditText box, TextView error)
    {
        var text = box.Text ?? "";
        if (text.Length == 0) return;
        try
        {
            await _session.UnlockAsync(text);
            box.Text = "";
            _dialog?.Dismiss();
            AfterUnlock();
        }
        catch (Exception ex) when (ex is Crypto.VaultKeyException or VaultThrottledException or InvalidOperationException)
        {
            error.Text = ex is VaultThrottledException t ? $"Too many tries. Wait {Math.Ceiling(t.RetryAfter.TotalSeconds)} s." : "That is not the vault password.";
            error.Visibility = ViewStates.Visible;
        }
    }

    private async Task UnlockWithDeviceAsync(TextView error)
    {
        try
        {
            if (!await _session.UnlockWithDeviceAsync()) return;
            _dialog?.Dismiss();
            AfterUnlock();
        }
        catch (Exception ex) when (ex is VaultDeviceUnlockException or InvalidOperationException)
        {
            error.Text = ex.Message;
            error.Visibility = ViewStates.Visible;
        }
    }

    // ---- Fill -----------------------------------------------------------------------------------------------

    private void AfterUnlock()
    {
        _session.Touch();
        var matches = VaultAutofill.Match(_store.Items(), _form.Target);
        if (_mode == Mode.Unlock && matches.Count > 0)
        {
            var response = new FillResponse.Builder();
            foreach (var match in matches.Take(5)) response.AddDataset(AutofillResponses.Dataset(this, _form, match));
            response.AddDataset(AutofillResponses.AuthDataset(this, _form, Create(this, _form, Mode.Pick), "Choose from Helm Vault…"));
            Answer(response.Build()!);
            return;
        }
        ShowPicker(matches);
    }

    private void ShowPicker(IReadOnlyList<AutofillMatch> matches)
    {
        var everyone = _store.Items().Where(e => e.Item.Kind == VaultItemKind.Login && e.Item.Password is { Length: > 0 })
            .OrderBy(e => e.Item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (everyone.Count == 0)
        {
            Fail("The vault has no logins yet.");
            return;
        }
        var layout = Column();
        var search = new EditText(this) { Hint = "Search", InputType = InputTypes.ClassText, ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants };
        layout.AddView(search, Wide());
        var list = new ListView(this);
        layout.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(320)));
        // An app is remembered by default; a site only when asked (a look-alike site must never be "remembered").
        var remember = new CheckBox(this)
        {
            Text = _form.Target.Host is { } host ? $"Also offer it on {host} next time" : "Offer it in this app next time",
            Checked = _form.Target.Host is null,
        };
        layout.AddView(remember);

        var shown = new List<VaultEntry>();
        void Filter(string? text)
        {
            var terms = Helm.Core.Text.TextSearch.Terms(text);
            shown.Clear();
            shown.AddRange(terms.Count == 0
                ? matches.Select(m => m.Entry).Concat(everyone.Where(e => matches.All(m => m.Entry.Uid != e.Uid)))
                : everyone.Where(e => Helm.Core.Text.TextSearch.Score(terms, e.Item.Title, e.Item.Username) > 0));
            list.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1,
                shown.Select(e => e.Item.Username is { } user ? $"{e.Item.Title}  ·  {user}" : e.Item.Title).ToList());
        }
        Filter(null);
        search.TextChanged += (_, _) => Filter(search.Text);
        list.ItemClick += (_, e) =>
        {
            var entry = shown[e.Position];
            if (remember.Checked)
            {
                try
                {
                    if (_form.Target.Host is { } site) RememberSite(entry, site);
                    else if (_form.Target.AppId is { Length: > 0 } app) VaultAutofill.RememberApp(_store, entry.Uid, VaultAutofill.AndroidAppScheme, app);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Crypto.VaultKeyException)
                {
                    _logger?.LogWarning(ex, "Could not remember the app for a login");
                }
            }
            var dataset = AutofillResponses.Dataset(this, _form, new AutofillMatch(entry, 0));
            if (_mode == Mode.Pick) Answer(dataset);
            else Answer(new FillResponse.Builder().AddDataset(dataset)!.Build()!);
        };

        _dialog = new AlertDialog.Builder(this)
            .SetTitle("Choose a login")!
            .SetView(layout)!
            .SetNegativeButton("Cancel", (_, _) => Cancel())!
            .SetOnCancelListener(new Listener(Cancel))!
            .Create()!;
        _dialog.Show();
    }

    private void RememberSite(VaultEntry entry, string host)
    {
        if (VaultAutofill.Match([entry], new AutofillTarget(host)).Count > 0) return;
        _store.Save(entry.Uid, entry.Item with { Fields = [.. entry.Item.Fields, new VaultField("Website", "https://" + host, VaultFieldKind.Url)] });
    }

    private void Answer(Java.Lang.Object result)
    {
        _answered = true;
        var reply = new Intent();
        reply.PutExtra(AutofillManager.ExtraAuthenticationResult, (IParcelable)result);
        SetResult(Result.Ok, reply);
        Finish();
    }

    private void Cancel()
    {
        if (_answered) return;
        SetResult(Result.Canceled);
        Finish();
    }

    private void Fail(string message)
    {
        Toast.MakeText(this, message, ToastLength.Long)?.Show();
        Cancel();
    }

    // ---- views ------------------------------------------------------------------------------------------------

    private LinearLayout Column()
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(Dp(20), Dp(8), Dp(20), 0);
        return layout;
    }

    private TextView Text(string text, bool secondary = false)
    {
        var view = new TextView(this) { Text = text, TextSize = secondary ? 13 : 15 };
        view.SetPadding(0, Dp(4), 0, Dp(4));
        if (secondary) view.Alpha = 0.75f;
        return view;
    }

    private static LinearLayout.LayoutParams Wide() => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1));

    private sealed class Listener(Action action) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => action();
    }
}
