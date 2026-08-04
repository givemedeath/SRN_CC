using SRN.CC.Core.Preview;
using SRN.CC.Preview.Render;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Single extension point that turns a successful <see cref="PreviewResult"/> into whatever
/// <see cref="PreviewContentViewModel"/> the slot should host, per architecture decision A3.
///
/// This is deliberately the one file three Wave-3 slices (S11 model viewport, S12 image zoom/pan,
/// S13 audio transport) will each add a small, additive branch to once their own content VM exists.
/// To keep those edits low-conflict, extend the switch expression below by inserting ONE new arm
/// ABOVE the <c>_ =&gt;</c> default — each arm is a self-contained pattern (on <c>result.Payload</c>'s
/// type, or on <c>result.Family</c> when there is no dedicated payload type) that a sibling slice can
/// add without touching any other arm:
///
/// <code>
/// // S11 (needs ModelScenePayload from Wave 2):
/// ModelScenePayload scene => new ModelViewportViewModel(scene.Scene),
///
/// // S12 / S13 (no dedicated payload type; keyed on Family instead):
/// _ when result.Family == PreviewFamily.Image => new ImageContentViewModel(result),
/// _ when result.Family == PreviewFamily.Audio => new AudioContentViewModel(result),
/// </code>
///
/// Until those land, every family — including Model — falls back to <see cref="TextContentViewModel"/>
/// wrapping <see cref="PreviewResult.FormattedContent"/>, which is exactly today's behavior.
/// </summary>
public static class PreviewContentFactory
{
    public static PreviewContentViewModel Create(PreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Payload switch
        {
            ModelScenePayload scene => new ModelViewportViewModel(scene.Scene),
            _ when result.Family == PreviewFamily.Image => new ImageContentViewModel(result),
            _ when result.Family == PreviewFamily.Audio => new AudioContentViewModel(result),

            _ => new TextContentViewModel(result.Family, result.FormattedContent),
        };
    }
}
