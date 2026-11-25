using Android.Content;
using Android.Views.Autofill;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Vault;

/// <summary>Whether Helm is Android's autofill service, and the way to Android's screen that changes it.</summary>
public partial class VaultAutofillCard : UserControl
{
    public VaultAutofillCard()
    {
        InitializeComponent();
    }

    private void OnResumed(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAutofill);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Helm.Core.Platform.ActivityHost.Resumed += OnResumed;
        UpdateAutofill();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The activity outlives the sheet: do not stay subscribed.
        Helm.Core.Platform.ActivityHost.Resumed -= OnResumed;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Whether Helm is Android's autofill service now (it changes in Android's settings, not here).</summary>
    private void UpdateAutofill()
    {
        var manager = AndroidApp.Context.GetSystemService(Java.Lang.Class.FromType(typeof(AutofillManager))) as AutofillManager;
        if (manager is null || !manager.IsAutofillSupported)
        {
            AutofillState.Text = "This phone does not support autofill.";
            AutofillButton.IsVisible = false;
            return;
        }
        var on = manager.HasEnabledAutofillServices;
        AutofillState.Text = on ? "Helm fills passwords on this phone." : "Another app (or none) fills passwords now.";
        AutofillButton.Content = on ? "Autofill settings…" : "Use Helm for autofill…";
    }

    /// <summary>Android's own screen for choosing the autofill service, with Helm proposed.</summary>
    private void Autofill_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionRequestSetAutofillService, Android.Net.Uri.Parse("package:" + AndroidApp.Context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApp.Context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            var intent = new Intent(Android.Provider.Settings.ActionSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApp.Context.StartActivity(intent);
        }
    }
}
