using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>Synced settings kept in memory.</summary>
internal sealed class MemorySettings<T> : ISyncedSettings<T> where T : class, IVersionedSettings, new()
{
    public T Current { get; } = new();

    public event EventHandler<T>? Changed;

    public void Update(Action<T> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, Current);
    }

    public void Flush() { }
}
