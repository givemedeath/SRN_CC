using NUnit.Framework;

namespace SRN.CC.CorpusTests;

/// <summary>
/// The single place the opt-in corpus tier decides whether it may run, replacing three
/// near-but-not-quite-identical <c>[SetUp]</c> blocks (<c>CorpusIndexAcceptanceTests</c>,
/// <c>Render/ModelPreviewCorpusTests</c>, <c>Render/ModelPreviewWorkingSetTests</c>) and finally
/// wiring <c>SRNCC_REQUIRE_CORPUS</c>, which <c>PLAN.md:229</c> has specified since milestone 1 but
/// which no code in this repository has ever read.
/// </summary>
/// <remarks>
/// <para>Environment variables, all read with the repository's existing truthiness convention
/// (<c>1</c> or <c>true</c>, case-insensitive; anything else — including unset — is false):</para>
/// <list type="bullet">
/// <item><description><c>SRNCC_CORPUS_ROOT</c> — directory containing the real HAK corpus.</description></item>
/// <item><description><c>SRNCC_RUN_CORPUS</c> — opt in to the corpus tier.</description></item>
/// <item><description><c>SRNCC_REQUIRE_CORPUS</c> — opt in <b>and</b> demand the corpus actually be
/// there: a missing or non-existent <c>SRNCC_CORPUS_ROOT</c> becomes a hard failure instead of a
/// silent ignore. Because <see cref="IsRequired"/> implies <see cref="IsEnabled"/>, setting this
/// alone is sufficient and the requirement cannot be defeated by forgetting
/// <c>SRNCC_RUN_CORPUS</c>.</description></item>
/// <item><description><c>SRNCC_RUN_GPU</c> — opt in to the real-GPU-context investigation.</description></item>
/// <item><description><c>SRNCC_RUN_IDLE_PROBE</c> — opt in to the 30-second idle working-set probe.</description></item>
/// </list>
/// <para>
/// These flags are read on every access rather than cached in a static initializer, so a fixture that
/// sets one for the duration of a test observes it, and so the gate cannot be poisoned by whichever
/// fixture happened to touch it first.
/// </para>
/// </remarks>
internal static class CorpusGate
{
    /// <summary>Name of the variable pointing at the corpus directory.</summary>
    public const string CorpusRootVariable = "SRNCC_CORPUS_ROOT";

    /// <summary>Name of the variable that opts in to the corpus tier.</summary>
    public const string RunCorpusVariable = "SRNCC_RUN_CORPUS";

    /// <summary>Name of the variable that makes a missing corpus a hard failure.</summary>
    public const string RequireCorpusVariable = "SRNCC_REQUIRE_CORPUS";

    /// <summary>Name of the variable that opts in to the real-GPU-context investigation.</summary>
    public const string RunGpuVariable = "SRNCC_RUN_GPU";

    /// <summary>Name of the variable that opts in to the 30-second idle working-set probe.</summary>
    public const string RunIdleProbeVariable = "SRNCC_RUN_IDLE_PROBE";

    /// <summary>
    /// True when <c>SRNCC_REQUIRE_CORPUS</c> is set: a missing corpus must fail, never ignore.
    /// </summary>
    public static bool IsRequired => IsFlagSet(RequireCorpusVariable);

    /// <summary>
    /// True when the corpus tier is opted in, either explicitly via <c>SRNCC_RUN_CORPUS</c> or
    /// implicitly because <see cref="IsRequired"/> is set.
    /// </summary>
    public static bool IsEnabled => IsFlagSet(RunCorpusVariable) || IsRequired;

    /// <summary>
    /// The raw <c>SRNCC_CORPUS_ROOT</c> value, unvalidated — may be null, blank, or point nowhere.
    /// </summary>
    public static string? Root => Environment.GetEnvironmentVariable(CorpusRootVariable);

    /// <summary>
    /// Resolves the corpus root or terminates the current test, in this precedence order:
    /// <list type="number">
    /// <item><description><see cref="IsRequired"/> and the root is missing or does not exist →
    /// <see cref="Assert.Fail(string)"/> naming the variable that is missing. This is checked
    /// <b>first</b>, before the enabled check, which is the whole point: the requirement outranks the
    /// opt-in and cannot be silently downgraded to an ignore.</description></item>
    /// <item><description>Not <see cref="IsEnabled"/> → <see cref="Assert.Ignore(string)"/>, the
    /// pre-existing default so the unfiltered developer run stays green without a corpus.</description></item>
    /// <item><description>Otherwise return the root — except that an enabled-but-not-required run
    /// whose root is absent still ignores, exactly as the three replaced <c>[SetUp]</c> blocks
    /// did.</description></item>
    /// </list>
    /// </summary>
    /// <returns>The validated, existing corpus root directory.</returns>
    public static string RequireCorpusRoot()
    {
        string? root = Root;
        bool rootIsUsable = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        if (IsRequired && !rootIsUsable)
        {
            Assert.Fail(
                $"{RequireCorpusVariable} is set, so the corpus tier must not be skipped, but " +
                $"{CorpusRootVariable} is " +
                (string.IsNullOrWhiteSpace(root)
                    ? "not set."
                    : $"set to '{root}', which is not an existing directory.") +
                $" Set {CorpusRootVariable} to the corpus directory, or unset {RequireCorpusVariable} " +
                "to allow the corpus tier to be ignored again.");
        }

        if (!IsEnabled)
        {
            Assert.Ignore(
                $"Corpus tests are ignored unless {RunCorpusVariable}=1 and {CorpusRootVariable} points to a valid directory.");
        }

        if (!rootIsUsable)
        {
            Assert.Ignore(
                $"Corpus tests are ignored unless {RunCorpusVariable}=1 and {CorpusRootVariable} points to a valid directory.");
        }

        return root!;
    }

    /// <summary>
    /// Ignores the current test unless <c>SRNCC_RUN_GPU</c> is set. Deliberately independent of
    /// <see cref="IsRequired"/>: requiring a corpus says nothing about whether a GPU is available.
    /// </summary>
    public static void RequireGpu()
    {
        if (!IsFlagSet(RunGpuVariable))
        {
            Assert.Ignore(
                $"Set {RunGpuVariable}=1 (in addition to {RunCorpusVariable}=1 / {CorpusRootVariable}) to run " +
                "the real-GPU-context investigation this test performs.");
        }
    }

    /// <summary>
    /// Ignores the current test unless <c>SRNCC_RUN_IDLE_PROBE</c> is set. Deliberately independent
    /// of <see cref="IsRequired"/>: the probe spends 30 seconds doing nothing on purpose, so it stays
    /// off inside an ordinary corpus run — a run that never opted into the probe must not be failed
    /// by it.
    /// </summary>
    public static void RequireIdleProbe()
    {
        if (!IsFlagSet(RunIdleProbeVariable))
        {
            Assert.Ignore(
                $"Set {RunIdleProbeVariable}=1 (in addition to {RunCorpusVariable}=1 / {CorpusRootVariable}) to " +
                "run the 30-second idle working-set probe.");
        }
    }

    /// <summary>
    /// The repository's environment-flag truthiness convention, preserved verbatim from the
    /// <c>[SetUp]</c> blocks this class replaces: <c>1</c> or <c>true</c>, case-insensitive.
    /// </summary>
    private static bool IsFlagSet(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
