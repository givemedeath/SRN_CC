using System.Text.Json.Nodes;

namespace SRN.CC.Core.Project;

public sealed record ProjectPreferences
{
    private readonly byte[]? _rawDocumentBytes;

    public JsonNode? OutputSettings { get; init; }
    public JsonNode? Filters { get; init; }
    public JsonNode? ComparisonPreferences { get; init; }
    public JsonObject? RawRootNode { get; init; }
    public string? RawDocumentText { get; init; }
    public byte[]? RawDocumentBytes => _rawDocumentBytes?.ToArray();

    public ProjectPreferences(
        JsonNode? outputSettings = null,
        JsonNode? filters = null,
        JsonNode? comparisonPreferences = null,
        JsonObject? rawRootNode = null,
        string? rawDocumentText = null,
        byte[]? rawDocumentBytes = null)
    {
        OutputSettings = outputSettings?.DeepClone();
        Filters = filters?.DeepClone();
        ComparisonPreferences = comparisonPreferences?.DeepClone();
        RawRootNode = rawRootNode?.DeepClone() as JsonObject;
        RawDocumentText = rawDocumentText;
        _rawDocumentBytes = rawDocumentBytes?.ToArray();
    }
}
