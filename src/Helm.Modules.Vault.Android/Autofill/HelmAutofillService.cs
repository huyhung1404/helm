using Android.App;
using Android.App.Assist;
using Android.Content;
using Android.OS;
using Android.Service.Autofill;
using Android.Text;
using Android.Views.Autofill;
using Android.Widget;
using Helm.Core;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vault.Autofill;

/// <summary>
/// Android's autofill service: when an app or a browser shows a sign-in form, Helm offers the vault's logins for that
/// site or app. While the vault is locked, the offer is "Unlock Helm Vault" (fingerprint or password, in
/// <see cref="AutofillActivity"/>); while it is open, the matching logins directly, plus "Choose from Helm Vault…".
/// Turned on in Android's settings (Vault settings → Autofill). Helm never fills its own screens.
/// </summary>
// A fixed name: Android remembers the chosen autofill service by it, so it must never change between versions.
[Service(Name = "com.huyhung1404.helm.vault.AutofillService", Label = "Helm Vault", Permission = "android.permission.BIND_AUTOFILL_SERVICE", Exported = true)]
[IntentFilter(["android.service.autofill.AutofillService"])]
[MetaData("android.autofill", Resource = "@xml/helm_autofill_service")]
public sealed class HelmAutofillService : AutofillService
{
    private const int MaxDatasets = 5;

    public override void OnFillRequest(FillRequest request, CancellationSignal cancellationSignal, FillCallback callback)
    {
        try
        {
            var structure = request.FillContexts[^1].Structure;
            var form = AutofillForm.Parse(structure);
            if (form is null || form.PackageName == PackageName)
            {
                callback.OnSuccess(null);
                return;
            }
            var session = HelmAndroidServices.Current.GetRequiredService<VaultSession>();
            // No vault on this phone (yet): offer nothing rather than a dead end on every sign-in form.
            if (session.State == VaultState.NotSetUp)
            {
                callback.OnSuccess(null);
                return;
            }
            if (session.State != VaultState.Unlocked)
            {
                // The vault key is not in memory: offer to unlock; the activity answers with the logins.
                var response = new FillResponse.Builder();
                AutofillResponses.SetAuthentication(response, form.Ids, AutofillActivity.Create(this, form, AutofillActivity.Mode.Unlock),
                    AutofillResponses.Row(this, "Unlock Helm Vault", form.Target.Host ?? "to fill this sign-in"));
                callback.OnSuccess(response.Build());
                return;
            }

            var store = HelmAndroidServices.Current.GetRequiredService<VaultStore>();
            var builder = new FillResponse.Builder();
            foreach (var match in VaultAutofill.Match(store.Items(), form.Target).Take(MaxDatasets))
                builder.AddDataset(AutofillResponses.Dataset(this, form, match));
            // Another login (or one for an app Helm does not know yet): the picker, which remembers the app.
            builder.AddDataset(AutofillResponses.AuthDataset(this, form, AutofillActivity.Create(this, form, AutofillActivity.Mode.Pick),
                "Choose from Helm Vault…"));
            session.Touch();
            callback.OnSuccess(builder.Build());
        }
        catch (Exception ex)
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger<HelmAutofillService>().LogError(ex, "Autofill request failed");
            callback.OnFailure((string?)null);
        }
    }

    /// <summary>Saving new logins from other apps is not offered (yet): add them in Helm.</summary>
    public override void OnSaveRequest(SaveRequest request, SaveCallback callback) => callback.OnSuccess();
}

/// <summary>A sign-in form found in another app's screen: where its fields are and who is asking.</summary>
public sealed record AutofillForm(AutofillId? Username, AutofillId? Password, AutofillId? Code, AutofillTarget Target, string PackageName)
{
    public AutofillId[] Ids => new[] { Username, Password, Code }.OfType<AutofillId>().ToArray();

    /// <summary>The form of a screen; null when it has neither a username nor a password field.</summary>
    public static AutofillForm? Parse(AssistStructure structure)
    {
        AutofillId? username = null, password = null, code = null;
        string? webDomain = null;
        void Visit(AssistStructure.ViewNode node)
        {
            if (!string.IsNullOrEmpty(node.WebDomain)) webDomain ??= node.WebDomain;
            if (node.AutofillId is { } id && node.AutofillType == global::Android.Views.AutofillType.Text)
            {
                var html = node.HtmlInfo;
                string? Attribute(string name) => html?.Attributes?.FirstOrDefault(a => string.Equals(a.First?.ToString(), name, StringComparison.OrdinalIgnoreCase))?.Second?.ToString();
                var isInput = html is null || string.Equals(html.Tag, "input", StringComparison.OrdinalIgnoreCase);
                var kind = !isInput ? AutofillFieldKind.None : AutofillFieldClassifier.Classify(node.GetAutofillHints(), HidesText(node.InputType),
                    Attribute("type"), Attribute("name") ?? Attribute("id"), Attribute("autocomplete"), node.IdEntry, node.Hint);
                if (kind == AutofillFieldKind.Password) password ??= id;
                else if (kind == AutofillFieldKind.Username) username ??= id;
                else if (kind == AutofillFieldKind.OneTimeCode) code ??= id;
            }
            for (var i = 0; i < node.ChildCount; i++) if (node.GetChildAt(i) is { } child) Visit(child);
        }
        for (var i = 0; i < structure.WindowNodeCount; i++) if (structure.GetWindowNodeAt(i)?.RootViewNode is { } root) Visit(root);
        if (username is null && password is null && code is null) return null;
        var package = structure.ActivityComponent?.PackageName ?? "";
        return new AutofillForm(username, password, code, new AutofillTarget(webDomain, webDomain is null ? package : null), package);
    }

    private static bool HidesText(InputTypes type)
    {
        var variation = type & InputTypes.MaskVariation;
        return (type & InputTypes.MaskClass) switch
        {
            InputTypes.ClassText => variation is InputTypes.TextVariationPassword or InputTypes.TextVariationWebPassword or InputTypes.TextVariationVisiblePassword,
            InputTypes.ClassNumber => variation == InputTypes.NumberVariationPassword,
            _ => false,
        };
    }
}

/// <summary>Builds what the autofill menu shows, with the API of each Android version (Presentations from 13 on).</summary>
internal static class AutofillResponses
{
    /// <summary>One row of the menu: a title and, smaller, a detail.</summary>
    public static RemoteViews Row(Context context, string title, string detail)
    {
        var row = new RemoteViews(context.PackageName, global::Android.Resource.Layout.SimpleListItem2);
        row.SetTextViewText(global::Android.Resource.Id.Text1, title);
        row.SetTextViewText(global::Android.Resource.Id.Text2, detail);
        return row;
    }

    /// <summary>A login ready to fill: its username and password (and the current one-time code).</summary>
    public static Dataset Dataset(Context context, AutofillForm form, AutofillMatch match)
    {
        var builder = NewDataset(Row(context, match.Title, match.Username ?? "Helm Vault"));
        var values = new List<(AutofillId, string)>();
        if (form.Username is { } u && match.Username is { Length: > 0 } name) values.Add((u, name));
        if (form.Password is { } p && match.Password is { Length: > 0 } secret) values.Add((p, secret));
        if (form.Code is { } c && match.Entry.Item.Totp is { } totp) values.Add((c, totp.Code(DateTimeOffset.UtcNow)));
        if (values.Count == 0 && form.Ids.FirstOrDefault() is { } any) values.Add((any, match.Username ?? ""));
        foreach (var (id, value) in values) SetValue(builder, id, value);
        return builder.Build()!;
    }

    /// <summary>A menu row that opens Helm (unlock or pick) and fills with what it returns.</summary>
    public static Dataset AuthDataset(Context context, AutofillForm form, Intent intent, string title)
    {
        var builder = NewDataset(Row(context, title, "Helm Vault"));
        // A dataset needs a field, even one that opens Helm first: its value comes from Helm's answer.
        foreach (var id in form.Ids) SetValue(builder, id, null);
        builder.SetAuthentication(Sender(context, intent));
        return builder.Build()!;
    }

    public static void SetAuthentication(FillResponse.Builder response, AutofillId[] ids, Intent intent, RemoteViews row)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            response.SetAuthentication(ids, Sender(context: null, intent), new Presentations.Builder().SetMenuPresentation(row).Build());
            return;
        }
#pragma warning disable CA1422 // The API of Android 8 to 12.
        response.SetAuthentication(ids, Sender(context: null, intent), row);
#pragma warning restore CA1422
    }

    private static Dataset.Builder NewDataset(RemoteViews row)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) return new Dataset.Builder(new Presentations.Builder().SetMenuPresentation(row).Build());
#pragma warning disable CA1422
        return new Dataset.Builder(row);
#pragma warning restore CA1422
    }

    private static void SetValue(Dataset.Builder builder, AutofillId id, string? value)
    {
        var autofill = value is null ? null : AutofillValue.ForText(value);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var field = new Field.Builder();
            if (autofill is not null) field.SetValue(autofill);
            builder.SetField(id, field.Build());
            return;
        }
#pragma warning disable CA1422
        builder.SetValue(id, autofill);
#pragma warning restore CA1422
    }

    /// <summary>
    /// The PendingIntent Android starts for authentication. Mutable: Android adds the form to it. Each gets its own
    /// request code, so two offers never share one.
    /// </summary>
    private static IntentSender Sender(Context? context, Intent intent)
    {
        var flags = PendingIntentFlags.CancelCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
        var pending = PendingIntent.GetActivity(context ?? global::Android.App.Application.Context, Interlocked.Increment(ref s_requests), intent, flags)!;
        return pending.IntentSender!;
    }

    private static int s_requests;
}
