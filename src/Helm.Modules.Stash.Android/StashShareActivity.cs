using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Helm.Core;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlertDialog = Android.App.AlertDialog;
using AndroidUri = Android.Net.Uri;

namespace Helm.Modules.Stash;

/// <summary>
/// "Helm Stash" in the share sheet: photos, videos, files (one or many) or text from any app go into the stash, with
/// a small progress dialog over that app, then start uploading so they reach the other devices. Native Android views,
/// not Avalonia: it opens instantly without Helm's main screen. Hidden while Stash is off (see <see cref="StashModule"/>).
/// </summary>
[Activity(Name = StashModule.ShareActivityName, Label = "Helm Stash", Exported = true, ExcludeFromRecents = true, NoHistory = true,
    TaskAffinity = "", Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
[IntentFilter([Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
public sealed class StashShareActivity : Activity
{
    private ILogger? _logger;
    private AlertDialog? _dialog;
    private TextView? _status;
    private ProgressBar? _bar;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            var services = HelmAndroidServices.Current;
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger<StashShareActivity>();
            var settings = services.GetRequiredService<ISettingsStoreFactory>();
            if (!IsEnabled(settings))
            {
                Toast.MakeText(this, "Turn on Stash in Helm to save into it.", ToastLength.Long)?.Show();
                Finish();
                return;
            }
            var uris = SharedUris(Intent);
            var text = Intent?.GetStringExtra(Intent.ExtraText);
            if (uris.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                Toast.MakeText(this, "There is nothing to save.", ToastLength.Short)?.Show();
                Finish();
                return;
            }
            ShowProgress(IsDark(settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.Theme));
            _ = SaveAsync(services, uris, text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not open the Stash share target");
            Toast.MakeText(this, $"Could not save to Stash: {ex.Message}", ToastLength.Long)?.Show();
            Finish();
        }
    }

    /// <summary>
    /// Files are read now, while this activity holds the permission to read them; the upload carries on after it
    /// closes (and with the next sync if Helm is stopped first).
    /// </summary>
    private async Task SaveAsync(IServiceProvider services, IReadOnlyList<AndroidUri> uris, string? text)
    {
        string message;
        try
        {
            var importer = services.GetRequiredService<StashImporter>();
            if (uris.Count > 0)
            {
                var sources = uris.Select(AndroidStashPlatform.FromUri).ToList();
                var progress = new Progress<StashImportProgress>(p =>
                {
                    if (_status is not null) _status.Text = p.Count == 1 ? $"Saving “{p.Name}”…" : $"Saving {p.Index} of {p.Count}: “{p.Name}”…";
                    if (_bar is null) return;
                    _bar.Indeterminate = p.Fraction is null;
                    _bar.Progress = (int)((p.Fraction ?? 0) * 100);
                });
                var result = await importer.ImportAsync(sources, progress).ConfigureAwait(true);
                message = result.Added.Count == 0 ? result.Summary : result.Summary.Replace("Added", "Saved to Stash:", StringComparison.Ordinal);
            }
            else
            {
                await importer.AddTextAsync(text!).ConfigureAwait(true);
                message = "Text saved to Stash.";
            }
            // Upload now, so the other devices get it soon; this does not hold the dialog.
            var sync = services.GetService<SyncEngine>();
            if (sync is not null) _ = Task.Run(() => sync.SyncNowAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving shared things to the stash failed");
            message = $"Could not save to Stash: {ex.Message}";
        }
        Toast.MakeText(this, message, ToastLength.Long)?.Show();
        _dialog?.Dismiss();
        Finish();
    }

    private static List<AndroidUri> SharedUris(Intent? intent)
    {
        var uris = new List<AndroidUri>();
        if (intent is null) return uris;
        if (intent.Action == Intent.ActionSendMultiple)
        {
            var list = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri)))
#pragma warning disable CA1422 // The typed overload exists from Android 13 only.
                : intent.GetParcelableArrayListExtra(Intent.ExtraStream);
#pragma warning restore CA1422
            if (list is not null) uris.AddRange(list.OfType<AndroidUri>());
        }
        else if (intent.Action == Intent.ActionSend)
        {
            var single = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri))) as AndroidUri
#pragma warning disable CA1422
                : intent.GetParcelableExtra(Intent.ExtraStream) as AndroidUri;
#pragma warning restore CA1422
            if (single is not null) uris.Add(single);
        }
        // Some apps put the files only in the clip data.
        if (uris.Count == 0 && intent.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
                if (clip.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
        }
        return uris;
    }

    /// <summary>Whether Stash is on, read from the saved settings (this can run before Helm's modules have started).</summary>
    private static bool IsEnabled(ISettingsStoreFactory settings) =>
        !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(StashIds.ModuleId, out var on) || on;

    private void ShowProgress(bool dark)
    {
        var theme = dark ? Android.Resource.Style.ThemeMaterialDialogAlert : Android.Resource.Style.ThemeMaterialLightDialogAlert;
        var ui = new ContextThemeWrapper(this, theme);
        var pad = Dp(20);
        var layout = new LinearLayout(ui) { Orientation = Orientation.Vertical };
        layout.SetPadding(pad, Dp(8), pad, Dp(8));
        _status = new TextView(ui) { Text = "Saving to Stash…", TextSize = 15 };
        _status.SetMaxLines(2);
        _status.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _status.SetTextColor(Color.ParseColor(dark ? "#FFFFFF" : "#1A1A1A"));
        layout.AddView(_status);
        _bar = new ProgressBar(ui, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Indeterminate = true };
        _bar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Color.ParseColor(dark ? "#4CC2FF" : "#0067C0"));
        layout.AddView(_bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) });

        var title = new TextView(ui) { Text = "Helm Stash", TextSize = 20 };
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        title.SetTextColor(Color.ParseColor(dark ? "#FFFFFF" : "#1A1A1A"));
        title.SetPadding(pad, Dp(18), pad, Dp(6));

        _dialog = new AlertDialog.Builder(ui, theme).SetCustomTitle(title)!.SetView(layout)!.SetCancelable(false)!.Create()!;
        var card = new Android.Graphics.Drawables.GradientDrawable();
        card.SetColor(Color.ParseColor(dark ? "#2B2B2B" : "#FBFBFB"));
        card.SetCornerRadius(Dp(12));
        _dialog.Window?.SetBackgroundDrawable(new Android.Graphics.Drawables.InsetDrawable(card, Dp(16)));
        _dialog.Show();
    }

    /// <summary>Helm's own theme choice (General → Theme); System follows the phone's dark theme.</summary>
    private bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => ((Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo) & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes,
    };

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);
}
