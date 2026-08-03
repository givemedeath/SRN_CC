using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var registry = new ResourceTypeRegistry();
            var cache = new SqliteCacheService();
            var indexService = new AssetIndexService(cache, registry);
            var dispatcher = new SourceReaderDispatcher(typeRegistry: registry);
            var hashService = new StreamingHashService(dispatcher);
            var resolver = new WorkspaceResolver(hashService);
            var workspaceService = new WorkspaceService(indexService, resolver);
            var projectStore = new ProjectStore(indexService, resolver);
            var settingsStore = new SettingsStore();

            var packer = new AssetPacker(dispatcher);
            var verifier = new BuildVerifier();
            var manifestGen = new ProvenanceManifestGenerator(registry);
            var publisher = new ArtifactPublisher();

            // Attempt to recover interrupted publication transactions before constructing the UI.
            publisher.RecoverPendingJournalAsync(Directory.GetCurrentDirectory()).GetAwaiter().GetResult();

            var orchestrator = new BuildOrchestrator(packer, verifier, manifestGen, publisher, registry, dispatcher);

            var previewEngine = new PreviewEngine(dispatcher, new IPreviewProvider[]
            {
                new MetadataPreviewProvider(registry),
                new BoundedHexPreviewProvider()
            });

            var vm = new MainWindowViewModel(
                workspaceService,
                projectStore,
                settingsStore,
                orchestrator,
                previewEngine,
                registry,
                dispatcher);

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
