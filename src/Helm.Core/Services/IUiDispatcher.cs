namespace Helm.Core.Services;

/// <summary>Marshals work to the WPF UI thread without modules depending on Helm.App.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);
}
