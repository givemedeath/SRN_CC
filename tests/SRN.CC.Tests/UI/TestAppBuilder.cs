using Avalonia;
using Avalonia.Headless;
using SRN.CC.Tests.UI;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SRN.CC.Tests.UI;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SRN.CC.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
