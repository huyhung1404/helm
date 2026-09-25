using System.Windows;
using System.Windows.Threading;
using Helm.Core.Services;

namespace Helm.App.Services;

internal sealed class UiDispatcher : IUiDispatcher
{
    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public bool CheckAccess() => Dispatcher.CheckAccess();

    public void Post(Action action) => Dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) => CheckAccess()
        ? RunInline(action)
        : Dispatcher.InvokeAsync(action).Task;

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
