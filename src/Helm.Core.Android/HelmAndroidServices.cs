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
}
