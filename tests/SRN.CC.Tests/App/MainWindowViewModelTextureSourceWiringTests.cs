using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.Services;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// Proves slice S15's App wiring for architecture decision A7's late-bound texture-source
/// accessor: a shared <see cref="TextureSourceHolder"/> is refreshed by
/// <see cref="MainWindowViewModel.LoadWorkspaceStateAsync"/> on every successful workspace load,
/// and the designer-preview parameterless constructor remains null-safe (it never receives a
/// holder, so the accessor path is simply inert rather than throwing).
/// </summary>
[TestFixture]
public class MainWindowViewModelTextureSourceWiringTests
{
    private static readonly IResourceTypeRegistry Registry = new ResourceTypeRegistry();

    [Test]
    public async Task LoadWorkspaceStateAsync_WithHolder_PopulatesNonNullTextureSource()
    {
        var holder = new TextureSourceHolder();
        var vm = CreateViewModel(holder);

        holder.Current.Should().BeNull("no workspace has loaded yet");
        vm.CurrentTextureSource.Should().BeNull();

        var state = BuildWorkspaceState();
        await vm.LoadWorkspaceStateAsync(state);

        holder.Current.Should().NotBeNull("LoadWorkspaceStateAsync must refresh the shared holder per A7");
        vm.CurrentTextureSource.Should().NotBeNull();
        vm.CurrentTextureSource.Should().BeSameAs(holder.Current, "the VM property mirrors the holder");
        vm.CurrentTextureSource.Should().BeOfType<WorkspaceTextureSource>();
    }

    [Test]
    public async Task LoadWorkspaceStateAsync_CalledTwice_RebuildsTextureSourceEachTime()
    {
        var holder = new TextureSourceHolder();
        var vm = CreateViewModel(holder);

        await vm.LoadWorkspaceStateAsync(BuildWorkspaceState());
        var firstTextureSource = holder.Current;
        firstTextureSource.Should().NotBeNull();

        await vm.LoadWorkspaceStateAsync(BuildWorkspaceState());
        var secondTextureSource = holder.Current;

        secondTextureSource.Should().NotBeNull();
        secondTextureSource.Should().NotBeSameAs(firstTextureSource,
            "each workspace load must rebuild the accessor's target over the new WorkspaceState");
    }

    [Test]
    public async Task LoadWorkspaceStateAsync_WithoutHolder_DoesNotThrow_AndAccessorStaysNull()
    {
        // The full constructor's textureSourceHolder parameter is optional; omitting it (as any
        // future caller that predates S15's wiring would) must remain safe.
        var vm = CreateViewModel(textureSourceHolder: null);

        Func<Task> act = async () => await vm.LoadWorkspaceStateAsync(BuildWorkspaceState());

        await act.Should().NotThrowAsync();
        vm.CurrentTextureSource.Should().BeNull();
    }

    [Test]
    public void ParameterlessDesignerConstructor_DoesNotThrow_AndTextureSourceIsNullSafe()
    {
        MainWindowViewModel? vm = null;
        Action act = () => vm = new MainWindowViewModel();

        act.Should().NotThrow("the XAML designer/synthetic-demo path must keep working without a real workspace");
        vm.Should().NotBeNull();
        vm!.CurrentTextureSource.Should().BeNull("no TextureSourceHolder is ever passed on this path");
    }

    private static MainWindowViewModel CreateViewModel(TextureSourceHolder? textureSourceHolder)
    {
        var dispatcher = new NullSourceReaderDispatcher();
        var previewEngine = new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>());

        return new MainWindowViewModel(
            new NotSupportedWorkspaceService(),
            new NotSupportedProjectStore(),
            new NotSupportedSettingsStore(),
            new NotSupportedBuildOrchestrator(),
            new NotSupportedArtifactPublisher(),
            previewEngine,
            Registry,
            dispatcher,
            textureSourceHolder);
    }

    private static WorkspaceState BuildWorkspaceState()
    {
        var identity = new AssetIdentity("c_rat", 2033); // dds
        Guid sourceId = Guid.NewGuid();
        var occurrence = new AssetOccurrence(identity, sourceId, new FolderFileLocator("textures/c_rat.dds"), "c_rat.dds", 4);
        var curated = new CuratedAsset(
            identity: identity,
            allOccurrences: new[] { occurrence },
            resolvedOccurrence: occurrence,
            pin: null,
            status: ResolutionStatus.Resolved,
            isSelected: false);
        var assetSource = new AssetSource(sourceId, AssetSourceKind.Folder, Path.GetTempPath());

        return new WorkspaceState(
            sources: new[] { assetSource },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: new[] { curated },
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());
    }

    // -------------------------------------------------------------------
    // Minimal fakes. LoadWorkspaceStateAsync (the only path exercised here) never calls into
    // _workspaceService/_projectStore/_settingsStore/_buildOrchestrator/_artifactPublisher, so
    // these throw if a future change starts relying on them without this test noticing.
    // -------------------------------------------------------------------

    private sealed class NullSourceReaderDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not needed for texture-source wiring tests.");
    }

    private sealed class NotSupportedWorkspaceService : IWorkspaceService
    {
        public WorkspaceState CurrentState => throw new NotSupportedException();

        public Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(
            IReadOnlyList<AssetSource> sources, IReadOnlyList<WinnerPin>? pins = null,
            SelectionState? selectionState = null, ProjectPreferences? preferences = null,
            bool isReadOnly = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(
            IReadOnlyList<Guid> sourceIdsInOrder, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(
            IEnumerable<Guid>? sourceIdsToRescan = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(
            Guid sourceId, string newPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceState> PinAsync(WinnerPin pin, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceState> UnpinAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceState> UpdateSelectionAsync(SelectionState newSelectionState, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceState> LoadProjectStateAsync(WorkspaceState newState, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NotSupportedProjectStore : IProjectStore
    {
        public Task<WorkspaceState> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsync(WorkspaceState state, string projectPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsAsync(WorkspaceState state, string targetProjectPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NotSupportedSettingsStore : ISettingsStore
    {
        public Task<ApplicationSettings> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NotSupportedBuildOrchestrator : IBuildOrchestrator
    {
        public Task<PublicationResult> ExecuteBuildAsync(
            WorkspaceState workspace, string destinationHakPath,
            IProgress<(string message, double progressFraction)>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NotSupportedArtifactPublisher : IArtifactPublisher
    {
        public Task<PublicationResult> PublishAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RecoverPendingJournalAsync(string journalDirectory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
