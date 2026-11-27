using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Ui;
using Helm.Modules.QuickCapture.Ui;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Quick Capture's page on Android: the shared <see cref="QuickCaptureView"/> (the same as on Windows), filling the page
/// area (<see cref="IFullPage"/>: the view scrolls itself). A phone has no shortcut: the Activation card says where the box
/// opens from instead (the share sheet, the Quick Settings tile, with "Add to Quick Settings" on Android 13+).
/// </summary>
public sealed class QuickCapturePage : UserControl, IFullPage, IQuickCapturePageHost
{
    private readonly TextBlock _tileResult = new() { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public QuickCapturePage(QuickCaptureModule module) => Content = new QuickCaptureView(new QuickCaptureViewModel(module), this);

    public bool HasShortcut => false;

    public Control CreateWaysToOpen()
    {
        var ways = new StackPanel { Spacing = 14 };
        ways.Children.Add(Way("Share to Helm",
            "In any app, tap Share and pick “Save to Helm”: the text or link is filled in, choose Note, Task or Debt and tap Save."));
        var tile = Way("Quick Settings tile",
            "Swipe down twice, tap the pencil and drag “Quick Capture” into the panel. One tap then opens an empty capture box.");
        // Android 13+ can place the tile on request; older versions only through the panel's edit mode.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var add = new Button
            {
                Classes = { "ghost" },
                Content = "Add to Quick Settings",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };
            add.Click += (_, _) => AddTile();
            tile.Children.Add(add);
            tile.Children.Add(_tileResult);
        }
        ways.Children.Add(tile);
        return ways;
    }

    private static StackPanel Way(string title, string text)
    {
        var way = new StackPanel { Spacing = 4 };
        way.Children.Add(new TextBlock { Text = title });
        way.Children.Add(new TextBlock { Text = text, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
        return way;
    }

    private void Say(string text)
    {
        _tileResult.Text = text;
        _tileResult.IsVisible = true;
    }

    private void AddTile()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            var context = AndroidApp.Context;
            var bar = (StatusBarManager?)context.GetSystemService(Context.StatusBarService);
            var iconId = context.Resources?.GetIdentifier("quick_capture_tile", "drawable", context.PackageName) ?? 0;
            bar?.RequestAddTileService(new ComponentName(context, QuickCaptureModule.TileName), new Java.Lang.String("Quick Capture"),
                Icon.CreateWithResource(context, iconId), context.MainExecutor!, new Result(code => Dispatcher.UIThread.Post(() => Say(code switch
                {
                    0 or 1 => "Quick Capture is in your Quick Settings panel.",
                    _ => "The tile was not added. You can still add it from the panel's edit mode.",
                }))));
        }
        catch (Exception ex)
        {
            Say($"Could not add the tile: {ex.Message}");
        }
    }

    /// <summary>StatusBarManager's answer: 0 = already added, 1 = added, 2+ = not added or failed.</summary>
    private sealed class Result(Action<int> done) : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        public void Accept(Java.Lang.Object? value) => done(value is Java.Lang.Integer i ? i.IntValue() : -1);
    }
}
