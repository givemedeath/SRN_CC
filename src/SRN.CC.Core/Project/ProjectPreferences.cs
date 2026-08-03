using System.Text.Json.Nodes;

namespace SRN.CC.Core.Project;

public sealed record ProjectPreferences
{
    public JsonNode? OutputSettings { get; init; }
    public JsonNode? Filters { get; init; }
    public JsonNode? ComparisonPreferences { get; init; }
    public JsonObject? RawRootNode { get; init; }

    public ProjectPreferences(
        JsonNode? outputSettings = null,
        JsonNode? filters = null,
        JsonNode? comparisonPreferences = null,
        JsonObject? rawRootNode = null)
    {
        OutputSettings = outputSettings?.DeepClone();
        Filters = filters?.DeepClone();
        ComparisonPreferences = comparisonPreferences?.DeepClone();
        RawRootNode = rawRootNode?.DeepClone() as JsonObject;
    }
}
