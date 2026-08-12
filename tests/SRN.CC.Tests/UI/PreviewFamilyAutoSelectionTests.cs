using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.UI;

/// <summary>
/// A preview slot opens an asset in the rendering that suits it, and remembers an explicit choice
/// for as long as that asset is on screen.
/// </summary>
/// <remarks>
/// <para>
/// Before this behaviour existed, every slot opened on <see cref="PreviewFamily.Metadata"/> and the
/// operator re-picked a family for every asset. The rule is: an occurrence arriving in a slot gets
/// its natural family; a family the operator picks sticks until a different occurrence arrives.
/// </para>
/// <para>
/// The engine here carries the real provider set, because the whole question is which provider
/// claims a given extension — a stubbed provider list would be asserting the fixture's opinion
/// rather than the application's. The dispatcher yields empty payloads, so previews fail to
/// <i>render</i>; that is deliberate and irrelevant, since family selection happens before a single
/// byte is read.
/// </para>
/// </remarks>
[TestFixture]
public class PreviewFamilyAutoSelectionTests
{
    // Type ids from src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Common/ResourceTypes.cs.
    private const ushort Tga = 3;
    private const ushort TwoDa = 2017;
    private const ushort Wav = 4;
    private const ushort Mdl = 2002;
    private const ushort Ssf = 2060;
    private const ushort UnknownButPackageable = 2078; // "lod" — no provider claims it.

    [TestCase(Tga, "crate01.tga", PreviewFamily.Image)]
    [TestCase(TwoDa, "appearance.2da", PreviewFamily.Text)]
    [TestCase(Wav, "footstep.wav", PreviewFamily.Audio)]
    [TestCase(Mdl, "tower.mdl", PreviewFamily.Model)]
    [TestCase(Ssf, "goblin.ssf", PreviewFamily.Tree)]
    public async Task AssigningAnOccurrence_SelectsTheFamilyThatFitsIt(
        ushort resourceType,
        string originalName,
        PreviewFamily expected)
    {
        Fixture fixture = new();

        await fixture.AssignAsync(resourceType, originalName);

        fixture.Slot.PreferredFamily.Should().Be(
            expected,
            "a slot should open an asset in the rendering that suits it rather than making the "
            + "operator pick one every time");
    }

    [Test]
    public async Task AssigningAnOccurrenceNoProviderClaims_FallsBackToMetadata()
    {
        Fixture fixture = new();

        await fixture.AssignAsync(UnknownButPackageable, "terrain.lod");

        fixture.Slot.PreferredFamily.Should().Be(
            PreviewFamily.Metadata,
            "an unrecognised type still has identity, size and origin worth showing; metadata is the "
            + "same fallback the engine itself lands on");
    }

    [Test]
    public async Task AnExplicitFamilyChoice_SurvivesForTheAssetItWasMadeOn()
    {
        Fixture fixture = new();
        await fixture.AssignAsync(Tga, "crate01.tga");
        fixture.Slot.PreferredFamily.Should().Be(PreviewFamily.Image);

        await fixture.Slot.SwitchFamilyCommand.ExecuteAsync("Hex");

        fixture.Slot.PreferredFamily.Should().Be(
            PreviewFamily.Hex,
            "auto-detection sets the opening rendering; it must not fight the operator afterwards");
    }

    [Test]
    public async Task AnExplicitFamilyChoice_DoesNotLeakOntoTheNextAsset()
    {
        Fixture fixture = new();
        await fixture.AssignAsync(Tga, "crate01.tga");
        await fixture.Slot.SwitchFamilyCommand.ExecuteAsync("Hex");

        await fixture.AssignAsync(Mdl, "tower.mdl");

        fixture.Slot.PreferredFamily.Should().Be(
            PreviewFamily.Model,
            "the choice was about the image, not a standing preference — a model arriving afterwards "
            + "should open as a model, not as the previous asset's hex dump");
    }

    [Test]
    public async Task SettingTheFamilyDirectly_RerendersTheOccupiedSlot()
    {
        Fixture fixture = new();
        await fixture.AssignAsync(Tga, "crate01.tga");

        // What the slot's picker does: assign the property rather than invoke the command.
        fixture.Slot.PreferredFamily = PreviewFamily.Hex;
        await fixture.Slot.FamilyReloadTask;

        fixture.Slot.PreferredFamily.Should().Be(PreviewFamily.Hex);
        fixture.Slot.IsLoading.Should().BeFalse("the re-render the picker started must have finished");
    }

    [Test]
    public void SettingTheFamilyOnAnEmptySlot_StartsNoRender()
    {
        Fixture fixture = new();

        fixture.Slot.PreferredFamily = PreviewFamily.Hex;

        fixture.Slot.FamilyReloadTask.IsCompleted.Should().BeTrue(
            "there is nothing assigned to re-render, so the change must not queue work");
        fixture.Slot.IsLoading.Should().BeFalse();
    }

    [Test]
    public async Task SwitchingFamilyOnAnEmptiedSlot_DoesNotAwaitAnEarlierRendersTask()
    {
        Fixture fixture = new();
        await fixture.AssignAsync(Tga, "crate01.tga");
        await fixture.Slot.SwitchFamilyCommand.ExecuteAsync("Hex");
        Task earlier = fixture.Slot.FamilyReloadTask;

        await fixture.Slot.AssignOccurrenceAsync(null, null, null);
        await fixture.Slot.SwitchFamilyCommand.ExecuteAsync("Text");

        fixture.Slot.PreferredFamily.Should().Be(PreviewFamily.Text);
        fixture.Slot.FamilyReloadTask.Should().NotBeSameAs(
            earlier,
            "there was nothing to render, so the command must not be gated on a previous render's task");
        fixture.Slot.FamilyReloadTask.IsCompleted.Should().BeTrue();
    }

    [Test]
    public void SelectableFamilies_OffersEveryRenderableFamilyAndNotUnknown()
    {
        PreviewSlotViewModel.SelectableFamilies.Should().Equal(
            PreviewFamily.Metadata,
            PreviewFamily.Image,
            PreviewFamily.Text,
            PreviewFamily.Audio,
            PreviewFamily.Tree,
            PreviewFamily.Model,
            PreviewFamily.Hex);

        PreviewSlotViewModel.SelectableFamilies.Should().NotContain(
            PreviewFamily.Unknown,
            "Unknown is a result a provider can report, never a rendering the operator can request");
    }

    /// <summary>A single slot over the real provider set and a payload-free dispatcher.</summary>
    private sealed class Fixture
    {
        private readonly AssetSource _source;

        public Fixture()
        {
            ResourceTypeRegistry registry = new();
            PreviewEngine engine = new(
                new EmptyDispatcher(),
                new IPreviewProvider[]
                {
                    new MetadataPreviewProvider(registry),
                    new ImagePreviewProvider(registry),
                    new TextPreviewProvider(registry),
                    new AudioPreviewProvider(registry),
                    new TreePreviewProvider(registry),
                    // Registered in the same order as the composition root: Mdl must come before the
                    // Hex fallback, or nothing would ever claim a .mdl.
                    new MdlPreviewProvider(registry, new MdlSceneBuilder(), new ModelSceneCache()),
                    new BoundedHexPreviewProvider()
                });

            _source = AssetSource.CreateHak(Path.GetFullPath("family_autoselect.hak"), 0);
            Slot = new PreviewSlotViewModel(0, engine, static _ => Task.FromResult(true));
        }

        public PreviewSlotViewModel Slot { get; }

        public async Task AssignAsync(ushort resourceType, string originalName)
        {
            AssetIdentity identity = new(Path.GetFileNameWithoutExtension(originalName), resourceType);
            AssetOccurrence occurrence = new(
                identity: identity,
                sourceId: _source.Id,
                locator: new HakEntryLocator(0),
                originalName: originalName,
                size: 64);

            CuratedAsset asset = new(
                identity,
                new[] { occurrence },
                occurrence,
                null,
                ResolutionStatus.Resolved,
                true);

            await Slot.AssignOccurrenceAsync(asset, occurrence, _source);
        }

        private sealed class EmptyDispatcher : ISourceReaderDispatcher
        {
            public Task<Stream> OpenOccurrenceAsync(
                AssetSource source,
                AssetOccurrence occurrence,
                CancellationToken cancellationToken = default)
                => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        }
    }
}
