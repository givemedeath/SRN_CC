using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;
using SRN.CC.Core.Project;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;

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
            RecoverStartupPublicationJournals(publisher, projectStore, desktop.Args);

            var orchestrator = new BuildOrchestrator(packer, verifier, manifestGen, publisher, registry, dispatcher);

            // MdlSceneBuilder/ModelSceneCache are process-lifetime singletons; the texture-source
            // accessor is left null here as an interim stopgap so the solution keeps compiling
            // across milestone-6 waves. Slice S15 replaces this with a late-bound accessor over
            // the loaded workspace's texture source per architecture decision A7.
            var mdlSceneBuilder = new MdlSceneBuilder();
            var modelSceneCache = new ModelSceneCache();

            var previewEngine = new PreviewEngine(dispatcher, new IPreviewProvider[]
            {
                new MetadataPreviewProvider(registry),
                new ImagePreviewProvider(registry),
                new TextPreviewProvider(registry),
                new AudioPreviewProvider(registry),
                new TreePreviewProvider(registry),
                new MdlPreviewProvider(registry, mdlSceneBuilder, modelSceneCache),
                new BoundedHexPreviewProvider()
            });

            var vm = new MainWindowViewModel(
                workspaceService,
                projectStore,
                settingsStore,
                orchestrator,
                publisher,
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

    private static void RecoverStartupPublicationJournals(
        ArtifactPublisher publisher,
        IProjectStore projectStore,
        IEnumerable<string>? startupArgs)
    {
        var recoveryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Directory.GetCurrentDirectory()
        };

        if (startupArgs != null)
        {
            foreach (string arg in startupArgs)
            {
                if (string.IsNullOrWhiteSpace(arg) || !File.Exists(arg))
                {
                    continue;
                }

                try
                {
                    string? projectDirectory = Path.GetDirectoryName(arg);
                    if (!string.IsNullOrWhiteSpace(projectDirectory))
                    {
                        recoveryDirectories.Add(Path.GetFullPath(projectDirectory));
                    }
                }
                catch
                {
                    // Ignore invalid startup path values; best-effort recovery only.
                }

                if (!string.Equals(Path.GetExtension(arg), ".srncc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var project = projectStore.LoadAsync(arg).GetAwaiter().GetResult();
                    if (project.Preferences.OutputSettings is JsonObject outputSettings)
                    {
                        if (outputSettings["targetHak"] is JsonValue targetHakNode
                            && targetHakNode.TryGetValue<string>(out string? targetHak)
                            && !string.IsNullOrWhiteSpace(targetHak))
                        {
                            string resolvedTargetHak = Path.IsPathFullyQualified(targetHak)
                                ? Path.GetFullPath(targetHak)
                                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(arg) ?? ".", targetHak));

                            string? configuredOutputDirectory = Path.GetDirectoryName(resolvedTargetHak);
                            if (!string.IsNullOrWhiteSpace(configuredOutputDirectory))
                            {
                                recoveryDirectories.Add(Path.GetFullPath(configuredOutputDirectory));
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore invalid project content while recovering journals.
                }
            }
        }

        foreach (string journalDirectory in recoveryDirectories)
        {
            try
            {
                publisher.RecoverPendingJournalAsync(journalDirectory).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore recovery failures at startup; callers can retry via project open.
            }
        }
    }
}
