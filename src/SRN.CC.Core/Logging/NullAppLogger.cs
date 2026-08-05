namespace SRN.CC.Core.Logging;

/// <summary>
/// The no-op <see cref="IAppLogger"/>. This is the default value for every trailing optional
/// <c>IAppLogger? logger = null</c> parameter in the application, so it must accept any input —
/// including a null exception, an empty category, and a null data dictionary — without throwing
/// and without allocating.
/// </summary>
public static class NullAppLogger
{
    /// <summary>The shared, stateless, thread-safe no-op logger.</summary>
    public static readonly IAppLogger Instance = new NoOpLogger();

    private sealed class NoOpLogger : IAppLogger
    {
        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null)
        {
            // Intentionally does nothing. See the type-level remarks.
        }
    }
}
