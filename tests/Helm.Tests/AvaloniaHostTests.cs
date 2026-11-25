using System.Windows.Controls;
using System.Windows.Forms.Integration;
using Helm.Ui.Windows;

namespace Helm.Tests;

public sealed class AvaloniaHostTests
{
    [Fact]
    public void A_page_that_throws_shows_a_message_instead_of_failing_the_window() => RunSta(() =>
    {
        // No real Avalonia in the test process (it changes the process's DPI handling for the WPF tests that follow).
        var starter = AvaloniaHost.Starter;
        AvaloniaHost.Starter = () => { };
        try
        {
            var host = new AvaloniaHost();
            host.Show(() => throw new InvalidOperationException("broken page"));

            Assert.Null(host.Page);
            Assert.IsNotType<WindowsFormsHost>(host.Content);
            var texts = ((StackPanel)host.Content).Children.OfType<TextBlock>().Select(t => t.Text);
            Assert.Contains(texts, t => t.Contains("broken page"));
        }
        finally
        {
            AvaloniaHost.Starter = starter;
        }
    });

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
