using Avalonia;
using Avalonia.Headless;
using SRN.CC.CorpusTests.Performance;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SRN.CC.CorpusTests.Performance;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SRN.CC.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
