namespace Helm.Core;

/// <summary>
/// The Android app's one service provider, created on first use in the app process. Android can start parts of the
/// app without its activity (a home-screen widget, a broadcast), so those components get Helm's services here instead
/// of from the Avalonia app. Helm.App.Android configures the factory in <c>Application.OnCreate</c>, before any
/// component runs.
/// </summary>
public static class HelmAndroidServices
{
    private static Lazy<IServiceProvider>? s_provider;

    public static bool IsConfigured => s_provider is not null;

    public static IServiceProvider Current =>
        s_provider?.Value ?? throw new InvalidOperationException("Helm's Android services are not configured.");

    /// <summary>Sets the factory once per process; later calls are ignored.</summary>
    public static void Configure(Func<IServiceProvider> factory) =>
        Interlocked.CompareExchange(ref s_provider, new Lazy<IServiceProvider>(factory, LazyThreadSafetyMode.ExecutionAndPublication), null);
}

/// <summary>Intent extras the shell understands when something (e.g. a widget) opens the app.</summary>
public static class ShellIntents
{
    /// <summary>String extra: the id of the tool whose page the app should show.</summary>
    public const string ExtraModule = "com.huyhung1404.helm.extra.MODULE";

    /// <summary>String extra, with <see cref="ExtraModule"/>: what that tool's page should show (e.g. a word's id).</summary>
    public const string ExtraItem = "com.huyhung1404.helm.extra.ITEM";

    private static readonly object s_gate = new();
    private static (string Module, string Item)? s_item;

    /// <summary>Raised when an item is offered (the tool's page, if shown, takes it at once).</summary>
    public static event EventHandler? ItemOffered;

    /// <summary>Hands an item from an intent to a tool's page (the shell calls it before showing the page).</summary>
    public static void OfferItem(string module, string item)
    {
        lock (s_gate) s_item = (module, item);
        ItemOffered?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The item offered to this tool, once; null when there is none.</summary>
    public static string? TakeItem(string module)
    {
        lock (s_gate)
        {
            if (s_item is not { } offered || offered.Module != module) return null;
            s_item = null;
            return offered.Item;
        }
    }
}
