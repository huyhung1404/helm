using Avalonia;
using Avalonia.Headless;
using Helm.Core.Ui;

[assembly: AvaloniaTestApplication(typeof(Helm.Ui.Tests.TestApp))]

namespace Helm.Ui.Tests;

/// <summary>The app every headless test runs in: Helm's theme, real text layout (Skia + HarfBuzz), no window.</summary>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new HelmTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseSkia().UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
