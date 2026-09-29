using Android.Content;
using Android.Views.Autofill;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Vault;

public partial class VaultSettingsView : UserControl
{
    public VaultSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) => UpdateAutofill();
        Helm.Core.Platform.ActivityHost.Resumed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAutofill);
    }

    /// <summary>Whether Helm is Android's autofill service now (it changes in Android's settings, not here).</summary>
    private void UpdateAutofill()
    {
        var manager = AndroidApp.Context.GetSystemService(Java.Lang.Class.FromType(typeof(AutofillManager))) as AutofillManager;
        var state = this.FindControl<TextBlock>("AutofillState");
        var button = this.FindControl<Button>("AutofillButton");
        if (state is null || button is null) return;
        if (manager is null || !manager.IsAutofillSupported)
        {
            state.Text = "This phone does not support autofill.";
            button.IsVisible = false;
            return;
        }
        var on = manager.HasEnabledAutofillServices;
        state.Text = on ? "Helm fills passwords on this phone." : "Another app (or none) fills passwords now.";
        button.Content = on ? "Autofill settings…" : "Use Helm for autofill…";
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
