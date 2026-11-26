using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.QuickCapture;

public partial class QuickCapturePage : ModulePageBase
{
    private readonly QuickCaptureViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public QuickCapturePage()
        : this(HelmAndroidServices.Current.GetRequiredService<QuickCaptureViewModel>())
    {
    }

    public QuickCapturePage(QuickCaptureViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Android 13+ can place the tile on request; older versions only through the panel's edit mode.
        AddTileButton.IsVisible = OperatingSystem.IsAndroidVersionAtLeast(33);
    }

    private void AddTile_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            var context = AndroidApp.Context;
            var bar = (StatusBarManager?)context.GetSystemService(Context.StatusBarService);
            var iconId = context.Resources?.GetIdentifier("quick_capture_tile", "drawable", context.PackageName) ?? 0;
            bar?.RequestAddTileService(new ComponentName(context, QuickCaptureModule.TileName), new Java.Lang.String("Quick Capture"),
                Icon.CreateWithResource(context, iconId), context.MainExecutor!, new Result(code => Dispatcher.UIThread.Post(() => _viewModel.Message = code switch
                {
                    0 or 1 => "Quick Capture is in your Quick Settings panel.",
                    _ => "The tile was not added. You can still add it from the panel's edit mode.",
                })));
        }
        catch (Exception ex)
        {
            _viewModel.Message = $"Could not add the tile: {ex.Message}";
        }
    }

    /// <summary>StatusBarManager's answer: 0 = already added, 1 = added, 2+ = not added or failed.</summary>
    private sealed class Result(Action<int> done) : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        public void Accept(Java.Lang.Object? value) => done(value is Java.Lang.Integer i ? i.IntValue() : -1);
    }
}
