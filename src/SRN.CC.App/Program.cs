using System.Text;
using Avalonia;
using SRN.CC.App.Services;
using SRN.CC.Core.Startup;

namespace SRN.CC.App;

internal class Program
{
    /// <summary>
    /// Runs the startup preflight, writes the resulting report to stdout as JSON, and exits without
    /// ever creating a window.
    /// </summary>
    /// <remarks>
    /// This is what makes a clean-machine smoke test deterministic: the runner parses the report and
    /// asserts that every check ran and that nothing is blocking, independently of whether it can
    /// realise a window at all. Nothing but the JSON document goes to stdout — any human-facing text
    /// goes to stderr — so the output is safe to pipe straight into a parser.
    /// </remarks>
    public const string PreflightOnlyArgument = "--srncc-preflight-only";

    [STAThread]
    public static int Main(string[] args)
    {
        args ??= Array.Empty<string>();

        if (args.Any(arg => string.Equals(arg, PreflightOnlyArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunPreflightOnly(args);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunPreflightOnly(string[] args)
    {
        TrySetUtf8Output();

        string[] forwardedArgs = args
            .Where(arg => !string.Equals(arg, PreflightOnlyArgument, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        bool reportWritten = false;

        try
        {
            AppServices services = AppServices
                .CreateAsync(AppPaths.Default, forwardedArgs)
                .GetAwaiter().GetResult();

            try
            {
                Console.Out.Write(AppServices.SerializeStartupReport(services.StartupReport));
                Console.Out.Write('\n');
                Console.Out.Flush();
                reportWritten = true;
            }
            finally
            {
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (Exception ex)
        {
            // AppServices.CreateAsync turns every environmental failure into a preflight result, so
            // reaching here means something structural broke. Still emit a parseable document — a
            // smoke runner that cannot parse the output has nothing to report — but fail the exit
            // code, because this is not a survivable degradation.
            Console.Error.WriteLine($"Preflight failed: {ex}");

            if (reportWritten)
            {
                // The document is already on stdout and stdout carries exactly one. Appending a
                // second would make the pair unparseable, and the runner would report a JSON error
                // instead of the failure written to stderr above.
                return 1;
            }

            Console.Out.Write(AppServices.SerializeStartupReport(new StartupReport(new[]
            {
                new StartupCheckResult(
                    "preflight-host",
                    StartupCheckSeverity.Blocking,
                    "The startup preflight could not be run.",
                    new[] { $"{ex.GetType().FullName}: {ex.Message}" })
            })));
            Console.Out.Write('\n');
            Console.Out.Flush();
            return 1;
        }
    }

    private static void TrySetUtf8Output()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception)
        {
            // Some redirected-stdout configurations refuse an encoding change. JSON escapes every
            // non-ASCII character it needs to, so the document stays parseable either way.
        }
    }
}
