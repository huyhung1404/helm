using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Markup;

namespace Helm.Tests;

/// <summary>
/// The one STA thread holding the one WPF <see cref="Application"/> a test process may have (a second one throws), with
/// the resources App.xaml merges. View tests run their bodies on it, one at a time.
/// </summary>
internal static class WpfHost
{
    private static readonly Lazy<Dispatcher> s_dispatcher = new(Start);

    public static void Run(Action body)
    {
        Exception? error = null;
        s_dispatcher.Value.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        if (error is not null) throw new Exception(error.Message, error);
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Helm.Core.Windows;component/Ui/HelmStyles.xaml") });
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
