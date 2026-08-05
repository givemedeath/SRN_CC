using System.ComponentModel;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The comparison panel must mutate slot state only on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Slot state is bound to live UI, and Avalonia throws when bound state changes off the UI thread.
/// From a command handler that is not a reportable error — it takes the process down, which is how
/// this reached a user as "clicking the mode button crashes the program", with nothing in the log.
/// </para>
/// <para>
/// The panel used <c>ConfigureAwait(false)</c> throughout. That made the failure uneven and easy to
/// misread: a slot assignment invoked <em>from</em> the UI thread resumes there, so the first
/// assignment in a loop was always safe, and only the second and later ones — resumed on a pool
/// thread once the first completed — crossed over. Toggling the mode reassigns every slot at once,
/// so it hit the bad path every time while ordinary selection changes often did not.
/// </para>
/// <para>
/// Asserting "does not throw" would not catch this: the throw happens on a pool thread, where the
/// test cannot see it. Recording the thread each change notification arrives on is what makes the
/// invariant observable.
/// </para>
/// </remarks>
[TestFixture]
public class ComparisonPanelThreadAffinityTests
{
    [AvaloniaTest]
    public async Task TogglingMode_RaisesEverySlotChangeOnTheUiThread()
    {
        Fixture fixture = new();
        await fixture.Panel.UpdateSelectionAsync(fixture.Assets, fixture.SourceMap);

        List<string> offThread = fixture.WatchSlotChanges();

        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        offThread.Should().BeEmpty(
            "a slot property changing off the UI thread crashes the application rather than raising "
            + "something catchable");
    }

    [AvaloniaTest]
    public async Task TogglingModeTwice_StaysOnTheUiThread()
    {
        Fixture fixture = new();
        await fixture.Panel.UpdateSelectionAsync(fixture.Assets, fixture.SourceMap);

        List<string> offThread = fixture.WatchSlotChanges();

        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);
        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        offThread.Should().BeEmpty();
    }

    [AvaloniaTest]
    public async Task ASelectionChange_RaisesEverySlotChangeOnTheUiThread()
    {
        Fixture fixture = new();
        List<string> offThread = fixture.WatchSlotChanges();

        await fixture.Panel.UpdateSelectionAsync(fixture.Assets, fixture.SourceMap);

        offThread.Should().BeEmpty("the same hazard applies to the path the asset grid drives");
    }

    private sealed class Fixture
    {
        private readonly Dictionary<Guid, AssetSource> _sources = new();
        private int _ordinal;

        public Fixture()
        {
            PreviewEngine engine = new(new EmptyDispatcher(), Array.Empty<IPreviewProvider>());
            Panel = new ComparisonPanelViewModel(engine, static _ => Task.CompletedTask);
            Assets = [CreateAsset("asset_a", 3), CreateAsset("asset_b", 2), CreateAsset("asset_c", 2)];
        }

        public ComparisonPanelViewModel Panel { get; }

        public IReadOnlyList<CuratedAsset> Assets { get; }

        public IReadOnlyDictionary<Guid, AssetSource> SourceMap => _sources;

        /// <summary>
        /// Records the name of every slot property that changes on a thread other than the UI one.
        /// </summary>
        public List<string> WatchSlotChanges()
        {
            List<string> offThread = [];
            foreach (PreviewSlotViewModel slot in Panel.Slots)
            {
                slot.PropertyChanged += (_, e) =>
                {
                    if (!Dispatcher.UIThread.CheckAccess())
                    {
                        lock (offThread)
                        {
                            offThread.Add(e.PropertyName ?? "(unnamed)");
                        }
                    }
                };
            }

            return offThread;
        }

        private CuratedAsset CreateAsset(string resref, int occurrenceCount)
        {
            AssetIdentity identity = new(resref, 2000);
            List<AssetOccurrence> occurrences = new(occurrenceCount);
            for (int i = 0; i < occurrenceCount; i++)
            {
                AssetSource source = AssetSource.CreateHak(
                    Path.GetFullPath($"affinity_{resref}_{i}.hak"), _ordinal++);
                _sources[source.Id] = source;
                occurrences.Add(new AssetOccurrence(
                    identity, source.Id, new HakEntryLocator(i), $"{resref}.2da", 64 + i));
            }

            return new CuratedAsset(
                identity, occurrences, occurrences[0], null, ResolutionStatus.Resolved, true);
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
