using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Connector.Hosting;

/// <summary>The box's host name, as the other devices list it ("fairy").</summary>
internal sealed class MachineDeviceInfo : IDeviceInfo
{
    public string DeviceName => Environment.MachineName;
}

/// <summary>No UI thread: what a view model posts runs at once, on the caller's thread.</summary>
internal sealed class HeadlessUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
