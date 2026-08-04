using SRN.CC.Preview;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Parses NWN MTR material-directive files: a small, line-oriented key/value text format
/// (<c>texture0</c>..<c>texture3</c>, <c>envmap</c>, <c>bumpmap</c>, <c>parameter</c>,
/// <c>renderhint</c>, <c>customshadervs</c>/<c>customshaderfs</c>).
/// </summary>
/// <remarks>
/// This is a first-party, preview-layer heuristic reader (per architecture decision A8), not an
/// attested binary format reader, so it lives under <c>Render/</c> rather than in
/// <c>SRN.CC.Formats</c>. It is bounded and defensive: malformed or truncated input never throws;
/// problems are surfaced through <see cref="Diagnostics"/> instead. Both
/// <see cref="SRN.CC.Preview.DependencyAnalyzer"/> and the future texture-resolution pipeline share
/// this single parser so they cannot disagree about MTR contents.
/// </remarks>
public sealed class MtrDocument
{
    private const int MaximumLines = 10_000;
    private const int MaximumLineLength = 4_096;
    private const int MaximumDiagnostics = 64;
    private const int MaximumParameters = 256;

    private static readonly char[] TokenSeparators = { ' ', '\t', '=' };
    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private MtrDocument()
    {
    }

    public string? Texture0 { get; private init; }

    public string? Texture1 { get; private init; }

    public string? Texture2 { get; private init; }

    public string? Texture3 { get; private init; }

    public string? EnvironmentMap { get; private init; }

    public string? BumpMap { get; private init; }

    public string? RenderHint { get; private init; }

    public string? CustomShaderVertex { get; private init; }

    public string? CustomShaderFragment { get; private init; }

    public IReadOnlyDictionary<string, string> Parameters { get; private init; } = NoParameters;

    public IReadOnlyList<string> Diagnostics { get; private init; } = Array.Empty<string>();

    /// <summary>Textures in <c>texture0</c>..<c>texture3</c> declaration order; entries are null when absent.</summary>
    public IReadOnlyList<string?> Textures => new[] { Texture0, Texture1, Texture2, Texture3 };

    /// <summary>Returns the texture declared at <paramref name="index"/> (0..3), or null when absent or out of range.</summary>
    public string? GetTexture(int index) => index switch
    {
        0 => Texture0,
        1 => Texture1,
        2 => Texture2,
        3 => Texture3,
        _ => null,
    };

    /// <summary>
    /// Parses MTR directive text from <paramref name="bytes"/>. Never throws: malformed, truncated,
    /// or otherwise unparseable input yields an empty/partial document plus <see cref="Diagnostics"/>.
    /// </summary>
    public static MtrDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var diagnostics = new List<string>();

        if (bytes.IsEmpty)
        {
            AddDiagnostic(diagnostics, "MTR payload is empty.");
            return new MtrDocument { Diagnostics = diagnostics };
        }

        try
        {
            string text = PreviewStreamHelpers.DecodeTextPayload(bytes, out _);
            return ParseText(text, diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddDiagnostic(diagnostics, $"MTR payload could not be parsed: {ex.Message}");
            return new MtrDocument { Diagnostics = diagnostics };
        }
    }

    private static MtrDocument ParseText(string text, List<string> diagnostics)
    {
        text = PreviewStreamHelpers.NormalizeLineEndings(text);
        string[] lines = text.Split('\n');

        if (lines.Length > MaximumLines)
        {
            AddDiagnostic(diagnostics, $"MTR payload exceeds {MaximumLines} lines; remaining lines ignored.");
        }

        int consideredLines = Math.Min(lines.Length, MaximumLines);

        string? texture0 = null;
        string? texture1 = null;
        string? texture2 = null;
        string? texture3 = null;
        string? envMap = null;
        string? bumpMap = null;
        string? renderHint = null;
        string? shaderVs = null;
        string? shaderFs = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int lineIndex = 0; lineIndex < consideredLines; lineIndex++)
        {
            string rawLine = lines[lineIndex];
            if (rawLine.Length > MaximumLineLength)
            {
                AddDiagnostic(diagnostics, $"MTR line {lineIndex + 1} exceeds {MaximumLineLength} characters; skipped.");
                continue;
            }

            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line[0] == '#')
            {
                continue;
            }

            string[] tokens = line.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            string directive = tokens[0].ToLowerInvariant();
            switch (directive)
            {
                case "texture0":
                    texture0 = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "texture1":
                    texture1 = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "texture2":
                    texture2 = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "texture3":
                    texture3 = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "envmap":
                case "environmentmap":
                    envMap = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "bumpmap":
                    bumpMap = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "renderhint":
                    renderHint = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "customshadervs":
                    shaderVs = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "customshaderfs":
                    shaderFs = RequireValue(tokens, directive, lineIndex, diagnostics);
                    break;
                case "parameter":
                    ApplyParameter(tokens, lineIndex, parameters, diagnostics);
                    break;
                default:
                    // Unknown directive - tolerated per the plan; not an error.
                    break;
            }
        }

        return new MtrDocument
        {
            Texture0 = texture0,
            Texture1 = texture1,
            Texture2 = texture2,
            Texture3 = texture3,
            EnvironmentMap = envMap,
            BumpMap = bumpMap,
            RenderHint = renderHint,
            CustomShaderVertex = shaderVs,
            CustomShaderFragment = shaderFs,
            Parameters = parameters.Count == 0 ? NoParameters : parameters,
            Diagnostics = diagnostics,
        };
    }

    private static void ApplyParameter(
        string[] tokens,
        int lineIndex,
        Dictionary<string, string> parameters,
        List<string> diagnostics)
    {
        if (tokens.Length < 2)
        {
            AddDiagnostic(diagnostics, $"MTR line {lineIndex + 1}: 'parameter' directive is missing a name.");
            return;
        }

        if (parameters.Count >= MaximumParameters)
        {
            AddDiagnostic(diagnostics, $"MTR payload exceeds {MaximumParameters} parameters; remaining parameters ignored.");
            return;
        }

        string name = tokens[1];
        string value = tokens.Length > 2 ? string.Join(' ', tokens[2..]) : string.Empty;
        parameters[name] = value;
    }

    private static string? RequireValue(string[] tokens, string directive, int lineIndex, List<string> diagnostics)
    {
        if (tokens.Length < 2)
        {
            AddDiagnostic(diagnostics, $"MTR line {lineIndex + 1}: '{directive}' directive is missing a value.");
            return null;
        }

        return tokens[1];
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count < MaximumDiagnostics)
        {
            diagnostics.Add(message);
        }
    }
}
