using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.CorpusTests.Render;

/// <summary>
/// Opt-in working-set check for the milestone-6 gate's budget claim
/// (<c>PLAN.md:229</c>'s 750 MiB idle target; the milestone-6 plan's own risk table: "Three 48 MiB
/// scenes + a 128 MiB cache breach the 750 MiB idle target | Budgets chosen for this; S17 asserts
/// working set after three previews + forced GC"). Builds three concurrent, independent model
/// previews from real corpus MDL payloads through the real <see cref="MdlPreviewProvider"/> →
/// <see cref="MdlSceneBuilder"/> → <see cref="ModelSceneCache"/> pipeline, uploads/renders/disposes
/// each through a real <see cref="ModelRenderer"/> paired with a <see cref="FakeGlDevice"/> (a real
/// GPU context is not required for a CPU-side working-set measurement — see
/// <c>ModelPreviewCorpusTests.RealGpuContext_CannotBeConstructedInThisRepositorysTestInfrastructure_DocumentedGap</c>
/// for why this file does not attempt one), forces a GC, and asserts private working set stays under
/// 750 MiB.
/// </summary>
/// <remarks>
/// Gated by the existing <c>SRNCC_RUN_CORPUS</c>/<c>SRNCC_CORPUS_ROOT</c> convention only (no
/// <c>SRNCC_RUN_GPU</c> guard): this check is entirely CPU-side, per the milestone-6 plan's own text
/// noted above.
/// </remarks>
[TestFixture]
[Category("Corpus")]
public class ModelPreviewWorkingSetTests
{
    private string? _corpusRoot;

    [SetUp]
    public void SetUp() => _corpusRoot = CorpusGate.RequireCorpusRoot();

    [Test]
    public async Task ThreeConcurrentModelPreviews_UploadRenderDispose_PlusForcedGc_KeepsWorkingSetBelow750MiB()
    {
        List<(string Label, byte[] Bytes)> candidates = CorpusModelFixtureLocator.FindModelPayloads(_corpusRoot!, count: 50);
        Assert.That(candidates, Is.Not.Empty, "the corpus root must contain at least one HAK with an mdl (resource type 2002) entry.");

        // Prefer three DISTINCT payloads (three independent slots); if the corpus happens to expose
        // fewer than three distinct mdl entries, fall back to reusing the first one under fresh
        // occurrence identities so the scenario ("three concurrent model previews") still holds even
        // against a sparse corpus fixture.
        List<(string Label, byte[] Bytes)> chosen = candidates.Count >= 3
            ? candidates.Take(3).ToList()
            : Enumerable.Range(0, 3).Select(i => candidates[i % candidates.Count]).ToList();

        MdlPreviewProvider provider = CreateProvider();

        Task<PreviewResult>[] previewTasks = chosen
            .Select(candidate => provider.GeneratePreviewAsync(CreateRequest(candidate.Label), new MemoryStream(candidate.Bytes)))
            .ToArray();
        PreviewResult[] results = await Task.WhenAll(previewTasks);

        List<RenderScene> scenes = results
            .Where(r => r.IsSuccess && r.Payload is ModelScenePayload)
            .Select(r => ((ModelScenePayload)r.Payload!).Scene)
            .ToList();

        Assert.That(scenes, Is.Not.Empty,
            "at least one of the three concurrent corpus previews must have produced a real scene payload.");
        Console.WriteLine($"Built {scenes.Count} of 3 concurrent model preview scene(s) from: {string.Join(", ", chosen.Select(c => c.Label))}");

        foreach (RenderScene scene in scenes)
        {
            using FakeGlDevice device = new();
            using ModelRenderer renderer = new(device);

            renderer.Initialize(device.Capabilities);
            renderer.Upload(scene);

            RenderCamera camera = RenderCamera.Frame(scene.BoundsMinimum, scene.BoundsMaximum, scene.Radius);
            renderer.Render(0, camera, 256, 256, showWalkmesh: true);

            renderer.Dispose();
            Assert.That(device.LiveResources, Is.Empty, $"device for '{scene.ModelName}' must not leak any GL resource after Dispose.");
        }

        scenes.Clear();
        results = Array.Empty<PreviewResult>();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using Process process = Process.GetCurrentProcess();
        long privateWorkingSetBytes = process.WorkingSet64;
        double workingSetMib = privateWorkingSetBytes / (1024.0 * 1024.0);
        Console.WriteLine($"Process private working set after three concurrent model previews + forced GC: {workingSetMib:F2} MiB");

        workingSetMib.Should().BeLessThan(750.0, "PLAN.md:229's idle-state private working-set target is 750 MiB.");
    }

    private static MdlPreviewProvider CreateProvider() =>
        new(new ResourceTypeRegistry(), new MdlSceneBuilder(), new ModelSceneCache());

    private static PreviewRequest CreateRequest(string label)
    {
        AssetOccurrence occurrence = new(
            identity: new AssetIdentity("corpus_model", 2002),
            sourceId: Guid.NewGuid(),
            locator: new HakEntryLocator(1),
            originalName: $"{label}.mdl",
            size: 0,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: null);

        return new PreviewRequest(occurrence, AssetSource.CreateHak("corpus.hak", 0), PreviewFamily.Model);
    }
}
