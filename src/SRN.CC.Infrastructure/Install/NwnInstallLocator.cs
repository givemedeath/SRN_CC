using System.Runtime.Versioning;
using Microsoft.Win32;
using SRN.CC.Core.Diagnostics;

namespace SRN.CC.Infrastructure.Install;

public sealed record NwnInstallLocation
{
    public string? InstallRoot { get; }
    public string? NwnBaseKeyPath { get; }
    public bool IsExplicitOverride { get; }
    public bool IsValid { get; }
    public IReadOnlyList<AssetDiagnosticRecord> Diagnostics { get; }
    public IReadOnlyList<string> AutoDiscoveredCandidates { get; }

    public NwnInstallLocation(
        string? installRoot,
        string? nwnBaseKeyPath,
        bool isExplicitOverride,
        bool isValid,
        IReadOnlyList<AssetDiagnosticRecord> diagnostics,
        IReadOnlyList<string> autoDiscoveredCandidates)
    {
        InstallRoot = installRoot;
        NwnBaseKeyPath = nwnBaseKeyPath;
        IsExplicitOverride = isExplicitOverride;
        IsValid = isValid;
        Diagnostics = diagnostics;
        AutoDiscoveredCandidates = autoDiscoveredCandidates;
    }
}

[SupportedOSPlatform("windows")]
public sealed class NwnInstallLocator
{
    public NwnInstallLocation Locate(string? explicitInstallRoot = null)
    {
        List<AssetDiagnosticRecord> diagnostics = new();
        List<string> candidates = new();

        if (!string.IsNullOrWhiteSpace(explicitInstallRoot))
        {
            string fullPath = Path.GetFullPath(explicitInstallRoot);
            string keyPath = Path.Combine(fullPath, "data", "nwn_base.key");
            if (File.Exists(keyPath))
            {
                return new NwnInstallLocation(
                    installRoot: fullPath,
                    nwnBaseKeyPath: keyPath,
                    isExplicitOverride: true,
                    isValid: true,
                    diagnostics: diagnostics,
                    autoDiscoveredCandidates: candidates);
            }
            else
            {
                AssetDiagnosticRecord diag = new(
                    DiagnosticCode.InvalidInstallRoot,
                    $"Explicit NWN install root '{fullPath}' is invalid. Could not find 'data\\nwn_base.key'.",
                    targetPath: fullPath);
                diagnostics.Add(diag);

                // Collect candidate suggestions without silently selecting one as primary
                List<string> suggestions = ProbeAutoDiscoveryCandidates(diagnostics);
                return new NwnInstallLocation(
                    installRoot: null,
                    nwnBaseKeyPath: null,
                    isExplicitOverride: true,
                    isValid: false,
                    diagnostics: diagnostics,
                    autoDiscoveredCandidates: suggestions);
            }
        }

        // Auto-discovery mode
        candidates = ProbeAutoDiscoveryCandidates(diagnostics);
        foreach (string candidate in candidates)
        {
            string keyPath = Path.Combine(candidate, "data", "nwn_base.key");
            if (File.Exists(keyPath))
            {
                return new NwnInstallLocation(
                    installRoot: candidate,
                    nwnBaseKeyPath: keyPath,
                    isExplicitOverride: false,
                    isValid: true,
                    diagnostics: diagnostics,
                    autoDiscoveredCandidates: candidates);
            }
        }

        AssetDiagnosticRecord notFoundDiag = new(
            DiagnosticCode.SourceNotFound,
            "NWN:EE installation root containing 'data\\nwn_base.key' was not found on this system.");
        diagnostics.Add(notFoundDiag);

        return new NwnInstallLocation(
            installRoot: null,
            nwnBaseKeyPath: null,
            isExplicitOverride: false,
            isValid: false,
            diagnostics: diagnostics,
            autoDiscoveredCandidates: candidates);
    }

    private static List<string> ProbeAutoDiscoveryCandidates(List<AssetDiagnosticRecord> diagnostics)
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

        // Probe 1: Default Steam location.
        // Derived from the well-known folder rather than written as a literal path: a machine whose
        // Program Files (x86) is not on C: would otherwise never get this probe, and an absolute
        // path literal is a release-audit violation once it is baked into the assembly.
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            string defaultSteam = Path.Combine(
                programFilesX86, "Steam", "steamapps", "common", "Neverwinter Nights");
            if (Directory.Exists(defaultSteam))
            {
                candidates.Add(Path.GetFullPath(defaultSteam));
            }
        }

        // Probe 2: Steam registry + libraryfolders.vdf (app ID 704450)
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
                {
                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        ParseLibraryFoldersVdf(vdfPath, candidates);
                    }

                    string defaultGamePath = Path.Combine(steamPath, "steamapps", "common", "Neverwinter Nights");
                    if (Directory.Exists(defaultGamePath))
                    {
                        candidates.Add(Path.GetFullPath(defaultGamePath));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new AssetDiagnosticRecord(DiagnosticCode.FileReadError, $"Steam registry probe warning: {ex.Message}"));
        }

        // Probe 3: GOG registry entries
        try
        {
            foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string view in new[] { @"Software\GOG.com\Games", @"Software\WOW6432Node\GOG.com\Games" })
                {
                    using var gamesKey = rootKey.OpenSubKey(view);
                    if (gamesKey == null) continue;

                    foreach (string subName in gamesKey.GetSubKeyNames())
                    {
                        using var gameKey = gamesKey.OpenSubKey(subName);
                        if (gameKey == null) continue;

                        string? gameName = gameKey.GetValue("GAMENAME") as string ?? gameKey.GetValue("GAMEPATH") as string;
                        string? path = gameKey.GetValue("PATH") as string;
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        {
                            if (subName.Contains("10953") || (gameName != null && gameName.Contains("Neverwinter Nights", StringComparison.OrdinalIgnoreCase)))
                            {
                                candidates.Add(Path.GetFullPath(path));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new AssetDiagnosticRecord(DiagnosticCode.FileReadError, $"GOG registry probe warning: {ex.Message}"));
        }

        return candidates.ToList();
    }

    private static void ParseLibraryFoldersVdf(string vdfPath, HashSet<string> candidates)
    {
        try
        {
            string[] lines = File.ReadAllLines(vdfPath);
            string? currentPath = null;
            bool has704450 = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentPath != null && has704450)
                    {
                        string gamePath = Path.Combine(currentPath, "steamapps", "common", "Neverwinter Nights");
                        if (Directory.Exists(gamePath))
                        {
                            candidates.Add(Path.GetFullPath(gamePath));
                        }
                    }

                    string[] parts = trimmed.Split('"');
                    if (parts.Length >= 4)
                    {
                        currentPath = parts[3].Replace(@"\\", @"\");
                        has704450 = false;
                    }
                }
                else if (trimmed.Contains("\"704450\""))
                {
                    has704450 = true;
                }
            }

            if (currentPath != null && has704450)
            {
                string gamePath = Path.Combine(currentPath, "steamapps", "common", "Neverwinter Nights");
                if (Directory.Exists(gamePath))
                {
                    candidates.Add(Path.GetFullPath(gamePath));
                }
            }
        }
        catch
        {
            // Ignore vdf parsing errors
        }
    }
}
