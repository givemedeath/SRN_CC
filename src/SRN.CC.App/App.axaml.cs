using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SRN.CC.App.Services;
using SRN.CC.App.Views;
using SRN.CC.Core.Startup;

namespace SRN.CC.App;

/// <summary>
/// The Avalonia application shell. Deliberately contains no service construction: the whole graph,
/// the startup preflight, and the logging pipeline live in <see cref="AppServices"/>, which is
/// constructible headlessly and therefore testable without a window.
/// </summary>
public partial class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices
                .CreateAsync(AppPaths.Default, desktop.Args ?? Array.Empty<string>())
                .GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow { DataContext = _services.CreateMainWindowViewModel() };

            // Exit, not ShutdownRequested: the latter is a request that any handler may veto by
            // setting Cancel, and disposing there would close the cache and the log sinks under an
            // application that then keeps running.
            desktop.Exit += (_, _) => _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
