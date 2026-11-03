using Android.Content;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;
using Activity = Android.App.Activity;
using ActivityAttribute = Android.App.ActivityAttribute;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using Toast = Android.Widget.Toast;
using ToastLength = Android.Widget.ToastLength;

namespace Helm.Modules.Vocab;

/// <summary>
/// "Add word" in the menu Android shows over selected text in any app: the selection goes straight into Vocabulary's
/// waiting list, with a short toast and no screen of its own (the app the user reads in stays in front).
/// </summary>
[Activity(Label = "Add word", Exported = true, NoHistory = true, ExcludeFromRecents = true, TaskAffinity = "",
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter([Intent.ActionProcessText], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class VocabSelectionActivity : Activity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var text = Intent?.GetCharSequenceExtra(Intent.ExtraProcessText) ?? "";
        Toast.MakeText(this, Add(text), ToastLength.Short)?.Show();
        Finish();
    }

    private static string Add(string text)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var module = services.GetRequiredService<IModuleHost<IAndroidModule>>().Find(VocabIds.ModuleId);
            if (module is { IsEnabled: false }) return "Vocabulary is turned off. Turn it on in Helm.";
            var result = services.GetRequiredService<VocabCaptureTarget>().Capture(text);
            // One word: analysed now on the phone instead of waiting for the PC; its meaning comes as a second toast.
            if (result.Saved && services.GetService<VocabQuickAnalyzer>() is { } quick && quick.WordToAnalyse(text) is { } id)
            {
                _ = AnalyseAsync(quick, id);
                return $"Added “{VocabText.Tidy(text)}” to Vocabulary; analysing it now…";
            }
            return result.Message;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Could not add a word from a selection: {ex}");
            return "Could not add the word: open Helm once, then try again.";
        }
    }

    /// <summary>Runs after the activity has gone: the toast is the application's.</summary>
    private static async Task AnalyseAsync(VocabQuickAnalyzer quick, string id)
    {
        string message;
        try
        {
            message = (await Task.Run(() => quick.AnalyzeAsync(id, CancellationToken.None))).Message;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Could not analyse a word on the phone: {ex.Message}");
            return;
        }
        new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() =>
            Toast.MakeText(Android.App.Application.Context, message, ToastLength.Long)?.Show());
    }
}
