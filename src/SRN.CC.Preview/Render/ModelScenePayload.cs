using SRN.CC.Core.Preview;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Wraps a built <see cref="RenderScene"/> as the typed <see cref="PreviewResult.Payload"/> for
/// model previews. Process-local only — per architecture decision A2, never enters
/// <c>preview_cache</c>.
/// </summary>
public sealed class ModelScenePayload : IPreviewPayload
{
    public string PayloadKind => "model-scene/v1";

    public long ApproximateByteSize => Scene.ApproximateByteSize;

    public required RenderScene Scene { get; init; }
}
