using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Project;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Startup;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Startup;

namespace SRN.CC.Tests.Startup;

/// <summary>Records every directory the check asked about and answers from a scripted map.</summary>
internal sealed class FakeArtifactPublisher : IArtifactPublisher
{
    private readonly HashSet<string> _recoverable;
    private readonly HashSet<string> _throwing;

    public FakeArtifactPublisher(
        IEnumerable<string>? recoverable = null,
        IEnumerable<string>? throwing = null)
    {
        _recoverable = new HashSet<string>(recoverable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _throwing = new HashSet<string>(throwing ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public List<string> ScannedDirectories { get; } = new();

    public Task<PublicationResult> PublishAsync(BuildPlan plan, string tempHakPath, string tempManifestPath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not publish.");

    public Task<bool> RecoverPendingJournalAsync(string journalDirectory, CancellationToken cancellationToken = default)
    {
        ScannedDirectories.Add(journalDirectory);

        if (_throwing.Contains(journalDirectory))
        {
            throw new IOException($"journal in '{journalDirectory}' is locked");
        }

        return Task.FromResult(_recoverable.Contains(journalDirectory));
    }
}

/// <summary>Serves canned projects by path, or throws for paths it does not know.</summary>
internal sealed class FakeProjectStore : IProjectStore
{
    private readonly Dictionary<string, WorkspaceState> _projects;

    public FakeProjectStore(Dictionary<string, WorkspaceState>? projects = null) =>
        _projects = projects ?? new Dictionary<string, WorkspaceState>(StringComparer.OrdinalIgnoreCase);

    public List<string> LoadedPaths { get; } = new();

    public Task<WorkspaceState> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        LoadedPaths.Add(projectPath);
        return _projects.TryGetValue(projectPath, out WorkspaceState? state)
            ? Task.FromResult(state)
            : throw new InvalidDataException($"no fake project registered for '{projectPath}'");
    }

    public Task SaveAsync(WorkspaceState state, string projectPath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not save projects.");

    public Task SaveAsAsync(WorkspaceState state, string targetProjectPath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not save projects.");

    public static WorkspaceState WithTargetHak(string targetHak) => new(
        Array.Empty<AssetSource>(),
        new Dictionary<Guid, SourceIndexSnapshot>(),
        Array.Empty<CuratedAsset>(),
        new SelectionState(),
        Array.Empty<WinnerPin>(),
        new ProjectPreferences(outputSettings: new JsonObject { ["targetHak"] = targetHak }));
}

[TestFixture]
public class PublicationJournalStartupCheckTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_JournalCheckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string MakeDir(string name)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private string MakeFile(string relativePath, string content = "{}")
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    [Test]
    public async Task NothingPending_IsOkAndScansTheWorkingDirectory()
    {
        string workingDirectory = MakeDir("work");
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        check.CheckId.Should().Be(PublicationJournalStartupCheck.Id);
        result.Severity.Should().Be(StartupCheckSeverity.Ok);
        publisher.ScannedDirectories.Should().Equal(workingDirectory);
    }

    [Test]
    public async Task StartupArgument_ContributesItsOwnDirectory()
    {
        string workingDirectory = MakeDir("work");
        string argFile = MakeFile(Path.Combine("opened", "thing.txt"));
        string argDirectory = Path.GetFullPath(Path.GetDirectoryName(argFile)!);
        FakeArtifactPublisher publisher = new(recoverable: new[] { argDirectory });

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            new[] { argFile },
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(argDirectory);
        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Details.Should().Contain($"recovered: {argDirectory}");
    }

    [Test]
    public async Task ProjectArgument_ContributesItsConfiguredOutputDirectory()
    {
        // This is the branch the original could never reach: it compared the extension against
        // ".srncc", and project files are ".srnccproj".
        string workingDirectory = MakeDir("work");
        string outputDirectory = MakeDir("published");
        string projectPath = MakeFile(Path.Combine("proj", "sample.srnccproj"));

        FakeProjectStore store = new(new Dictionary<string, WorkspaceState>(StringComparer.OrdinalIgnoreCase)
        {
            [projectPath] = FakeProjectStore.WithTargetHak(Path.Combine(outputDirectory, "out.hak"))
        });
        FakeArtifactPublisher publisher = new(recoverable: new[] { outputDirectory });

        PublicationJournalStartupCheck check = new(
            publisher,
            store,
            new[] { projectPath },
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        store.LoadedPaths.Should().Equal(projectPath);
        publisher.ScannedDirectories.Should().Contain(outputDirectory);
        result.Details.Should().Contain($"recovered: {outputDirectory}");
    }

    [Test]
    public async Task ProjectArgument_WithARelativeTargetHak_ResolvesAgainstTheProjectDirectory()
    {
        string workingDirectory = MakeDir("work");
        string projectPath = MakeFile(Path.Combine("proj", "sample.srnccproj"));
        string projectDirectory = Path.GetFullPath(Path.GetDirectoryName(projectPath)!);
        string expectedOutput = Path.GetFullPath(Path.Combine(projectDirectory, "dist"));

        FakeProjectStore store = new(new Dictionary<string, WorkspaceState>(StringComparer.OrdinalIgnoreCase)
        {
            [projectPath] = FakeProjectStore.WithTargetHak(Path.Combine("dist", "out.hak"))
        });
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            store,
            new[] { projectPath },
            currentDirectory: () => workingDirectory);

        await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(expectedOutput);
    }

    [Test]
    public async Task NonProjectExtension_DoesNotLoadAProject()
    {
        string workingDirectory = MakeDir("work");
        string argFile = MakeFile(Path.Combine("opened", "notes.srncc"));
        FakeProjectStore store = new();

        PublicationJournalStartupCheck check = new(
            new FakeArtifactPublisher(),
            store,
            new[] { argFile },
            currentDirectory: () => workingDirectory);

        await check.RunAsync();

        store.LoadedPaths.Should().BeEmpty();
    }

    [Test]
    public async Task RecentProjectPath_IsRecovered()
    {
        // The exit-criteria case: a journal reachable only through the recent-project list.
        string workingDirectory = MakeDir("work");
        string recentProjectDirectory = MakeDir("recent");
        string recentProjectPath = Path.Combine(recentProjectDirectory, "old.srnccproj");
        File.WriteAllText(recentProjectPath, "{}");

        FakeArtifactPublisher publisher = new(recoverable: new[] { recentProjectDirectory });

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            startupArgs: null,
            recentProjectPaths: () => new[] { recentProjectPath },
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(recentProjectDirectory);
        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Summary.Should().Contain("Recovered 1");
        result.Details.Should().Contain($"recovered: {recentProjectDirectory}");
    }

    [Test]
    public async Task RecentProjectPath_ThatIsItselfADirectory_IsUsedDirectly()
    {
        string workingDirectory = MakeDir("work");
        string recentDirectory = MakeDir("recentdir");
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            startupArgs: null,
            recentProjectPaths: () => new[] { recentDirectory },
            currentDirectory: () => workingDirectory);

        await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(recentDirectory);
    }

    [Test]
    public async Task RecentProjectPaths_AreReadWhenTheCheckRunsNotWhenItIsConstructed()
    {
        string workingDirectory = MakeDir("work");
        string lateDirectory = MakeDir("late");
        List<string> recent = new();
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            startupArgs: null,
            recentProjectPaths: () => recent,
            currentDirectory: () => workingDirectory);

        recent.Add(lateDirectory);
        await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(lateDirectory);
    }

    [Test]
    public async Task RecoveryFailure_IsDegradedAndNamesTheDirectory_RatherThanBeingSwallowed()
    {
        string workingDirectory = MakeDir("work");
        FakeArtifactPublisher publisher = new(throwing: new[] { workingDirectory });
        RecordingAppLogger logger = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            startupArgs: null,
            recentProjectPaths: null,
            currentDirectory: () => workingDirectory,
            logger: logger);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Details.Should().Contain(d => d.StartsWith($"recovery-failed: {workingDirectory}", StringComparison.Ordinal));
        logger.Entries.Should().Contain(e => e.ExceptionType == typeof(IOException).FullName);
    }

    [Test]
    public async Task UnreadableProject_IsReportedRatherThanSwallowed_AndTheRunContinues()
    {
        string workingDirectory = MakeDir("work");
        string projectPath = MakeFile(Path.Combine("proj", "broken.srnccproj"));
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            new[] { projectPath },
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        result.Details.Should().Contain(d => d.StartsWith($"unreadable-project: {projectPath}", StringComparison.Ordinal));
        publisher.ScannedDirectories.Should().Contain(workingDirectory);
    }

    [Test]
    public async Task NonExistentAndBlankArguments_AreIgnored()
    {
        string workingDirectory = MakeDir("work");
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            new[] { "   ", Path.Combine(_tempDir, "does-not-exist.srnccproj") },
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Ok);
        publisher.ScannedDirectories.Should().Equal(workingDirectory);
    }

    [Test]
    public async Task DuplicateDirectories_AreScannedOnce()
    {
        string workingDirectory = MakeDir("work");
        string argFile = MakeFile(Path.Combine("work", "a.txt"));
        FakeArtifactPublisher publisher = new();

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            new[] { argFile },
            recentProjectPaths: () => new[] { argFile },
            currentDirectory: () => workingDirectory);

        await check.RunAsync();

        publisher.ScannedDirectories.Should().Equal(workingDirectory);
    }

    [Test]
    public async Task NeverReturnsBlocking_EvenWhenEverythingFails()
    {
        string workingDirectory = MakeDir("work");
        FakeArtifactPublisher publisher = new(throwing: new[] { workingDirectory });

        PublicationJournalStartupCheck check = new(
            publisher,
            new FakeProjectStore(),
            currentDirectory: () => workingDirectory);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().NotBe(StartupCheckSeverity.Blocking);
    }

    [Test]
    public void NullArguments_Throw()
    {
        Action nullPublisher = () => _ = new PublicationJournalStartupCheck(null!, new FakeProjectStore());
        Action nullStore = () => _ = new PublicationJournalStartupCheck(new FakeArtifactPublisher(), null!);

        nullPublisher.Should().Throw<ArgumentNullException>();
        nullStore.Should().Throw<ArgumentNullException>();
    }
}
