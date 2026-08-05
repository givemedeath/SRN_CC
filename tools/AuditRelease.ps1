# tools/AuditRelease.ps1
# Release gate over a published tree: proves the release is the non-single-file layout the product
# requires, that every shipped file is explained and clean, that the license tree is intact, and
# (with -ZipPath) that PackRelease.ps1 is byte-reproducible.
[CmdletBinding()]
param(
    [string]$Root = "",
    [string]$ZipPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $repoRoot "artifacts/publish/win-x64"
}
$Root = [System.IO.Path]::GetFullPath($Root)

$publishPolicyPath = Join-Path $repoRoot "eng/publish-policy.json"
$releasePolicyPath = Join-Path $repoRoot "eng/release-policy.json"
$dependencyPolicyPath = Join-Path $repoRoot "eng/dependency-policy.json"

$script:violations = 0

function Add-Violation([string]$Message) {
    Write-Host "RELEASE AUDIT ERROR: $Message" -ForegroundColor Red
    $script:violations++
}

# Origins come from the deps manifest first. specialOrigins is an override of last resort only:
# writing a frozen literal for something the deps manifest already resolves (any native library, for
# example) turns the next package version bump into an audit failure that names the policy file
# instead of the bump.
function Add-DepsOrigin([hashtable]$Map, [string]$RelativePath, [string]$Origin) {
    $normalized = $RelativePath.Replace('\', '/')
    if ($Map.ContainsKey($normalized) -and $Map[$normalized] -ne $Origin) {
        Add-Violation "Published path '$normalized' has ambiguous deps-derived origins '$($Map[$normalized])' and '$Origin'."
    } else {
        $Map[$normalized] = $Origin
    }
}

foreach ($required in @($publishPolicyPath, $releasePolicyPath, $dependencyPolicyPath)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        Write-Error "Required policy is missing: $required"
        exit 1
    }
}
if (-not (Test-Path $Root -PathType Container)) {
    Write-Error "Release root does not exist: $Root"
    exit 1
}

$policy = Get-Content $publishPolicyPath -Raw | ConvertFrom-Json
$releasePolicy = Get-Content $releasePolicyPath -Raw | ConvertFrom-Json

Write-Host "Auditing release tree: $Root" -ForegroundColor Cyan

# --- 1. Shipped file set ------------------------------------------------------------------------
# Mirror what PackRelease.ps1 actually ships: the excluded files are audit evidence, not payload.
$excluded = @{}
foreach ($name in $releasePolicy.zipExcludedFiles) {
    $excluded[$name.Replace('\', '/').ToLowerInvariant()] = $true
}

$shipped = @()
foreach ($file in (Get-ChildItem -Path $Root -Recurse -File)) {
    $relPath = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
    if ($excluded.ContainsKey($relPath.ToLowerInvariant()) -or $excluded.ContainsKey($file.Name.ToLowerInvariant())) {
        continue
    }
    $shipped += [pscustomobject]@{ File = $file; RelativePath = $relPath }
}

# --- 2. Non-single-file layout ------------------------------------------------------------------
foreach ($marker in $releasePolicy.nonSingleFileMarkers) {
    $markerPath = Join-Path $Root $marker
    if (-not (Test-Path $markerPath -PathType Leaf)) {
        Add-Violation "Non-single-file marker '$marker' is missing from the release root; the tree looks like a single-file publish."
    }
}
if ($shipped.Count -le $releasePolicy.minimumFileCount) {
    Add-Violation "Release tree ships $($shipped.Count) file(s); a non-single-file publish must ship more than $($releasePolicy.minimumFileCount)."
}

$singleFileSources = @(Get-ChildItem -Path $repoRoot -Recurse -File -Include "*.csproj", "*.props", "*.targets" |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|artifacts|\.git)\\' })
foreach ($sourceFile in $singleFileSources) {
    $text = [System.IO.File]::ReadAllText($sourceFile.FullName)
    if ($text -match 'PublishSingleFile') {
        $relSource = $sourceFile.FullName.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/')
        Add-Violation "Build file '$relSource' declares PublishSingleFile; the release layout must stay non-single-file."
    }
}

# --- 3. Origins resolved from the deps manifest ---------------------------------------------------
$originMap = @{}
$depsPath = Join-Path $Root $policy.depsFile
if (-not (Test-Path $depsPath -PathType Leaf)) {
    Add-Violation "Published deps manifest is missing: '$($policy.depsFile)'."
} else {
    $deps = Get-Content $depsPath -Raw | ConvertFrom-Json
    $expectedTarget = ".NETCoreApp,Version=v10.0/$($policy.targetRID)"
    if ($deps.runtimeTarget.name -ne $expectedTarget) {
        Add-Violation "Expected deps runtime target '$expectedTarget', got '$($deps.runtimeTarget.name)'."
    }
    $target = $deps.targets.PSObject.Properties | Where-Object Name -eq $deps.runtimeTarget.name | Select-Object -First 1
    if (-not $target) {
        Add-Violation "The deps manifest has no resolved target '$($deps.runtimeTarget.name)'."
    } else {
        foreach ($library in $target.Value.PSObject.Properties) {
            $parts = $library.Name.Split('/', 2)
            $libraryId = $parts[0]
            $libraryMetadata = $deps.libraries.PSObject.Properties | Where-Object Name -eq $library.Name | Select-Object -First 1
            $libraryType = $libraryMetadata.Value.type

            if ($libraryType -eq "package") {
                $origin = "nuget:$($library.Name)"
            } elseif ($libraryType -eq "project") {
                $origin = "project:$libraryId"
            } elseif ($libraryType -eq "runtimepack") {
                $origin = "runtimepack:$($library.Name)"
            } else {
                Add-Violation "Unknown deps library type '$libraryType' for '$($library.Name)'."
                $origin = "unknown:$($library.Name)"
            }

            foreach ($assetGroup in @("runtime", "native", "resources")) {
                $assets = $library.Value.PSObject.Properties | Where-Object Name -eq $assetGroup | Select-Object -First 1
                foreach ($asset in $assets.Value.PSObject.Properties) {
                    Add-DepsOrigin $originMap ([System.IO.Path]::GetFileName($asset.Name)) $origin
                }
            }

            $runtimeTargets = $library.Value.PSObject.Properties | Where-Object Name -eq "runtimeTargets" | Select-Object -First 1
            foreach ($asset in $runtimeTargets.Value.PSObject.Properties) {
                if ($asset.Value.rid -and $asset.Value.rid -ne $policy.targetRID) {
                    continue
                }
                Add-DepsOrigin $originMap ([System.IO.Path]::GetFileName($asset.Name)) $origin
            }
        }
    }
}

# Overrides of last resort: only fill in what the deps manifest cannot explain (the apphost, the
# build-generated manifests, and the license/notice tree).
foreach ($special in $policy.specialOrigins.PSObject.Properties) {
    $normalized = $special.Name.Replace('\', '/')
    if (-not $originMap.ContainsKey($normalized)) {
        $originMap[$normalized] = $special.Value
    }
}

# --- 4. Required files, forbidden content ---------------------------------------------------------
$required = @{}
foreach ($path in $policy.requiredFiles) { $required[$path.Replace('\', '/')] = $false }
$forbiddenExtensions = @($policy.forbiddenExtensions | ForEach-Object { $_.ToLowerInvariant() })
$forbiddenAssemblies = @($policy.forbiddenAssemblies | ForEach-Object { $_.ToLowerInvariant() })
$absolutePathPattern = '[A-Za-z]:\\[ -~]{1,60}\\[ -~]{1,60}'

foreach ($item in $shipped) {
    $file = $item.File
    $relPath = $item.RelativePath
    $extension = $file.Extension.ToLowerInvariant()
    $name = $file.Name.ToLowerInvariant()

    if ($required.ContainsKey($relPath)) { $required[$relPath] = $true }
    if ($extension -in $forbiddenExtensions) {
        Add-Violation "Forbidden extension '$extension' found at '$relPath'."
    }
    if ($name -in $forbiddenAssemblies) {
        Add-Violation "Forbidden test/development assembly '$relPath'."
    }

    $origin = $null
    if ($originMap.ContainsKey($relPath)) {
        $origin = $originMap[$relPath]
    } elseif ($originMap.ContainsKey($file.Name)) {
        $origin = $originMap[$file.Name]
    } else {
        Add-Violation "No resolved origin explains '$relPath'."
        $origin = "unresolved"
    }

    if ($extension -in @(".dll", ".exe", ".json")) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
        $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
        $searchText = ($ascii + "`n" + $unicode).ToLowerInvariant()
        foreach ($fragment in $policy.forbiddenPathFragments) {
            if ($searchText.Contains($fragment.ToLowerInvariant())) {
                Add-Violation "Forbidden local path fragment '$fragment' is embedded in '$relPath'."
            }
        }
        if ($searchText.Contains($repoRoot.ToLowerInvariant())) {
            Add-Violation "Workspace absolute path is embedded in '$relPath'."
        }
        if (($origin.StartsWith("project:") -or $origin.StartsWith("build:")) -and ($ascii -match $absolutePathPattern -or $unicode -match $absolutePathPattern)) {
            Add-Violation "First-party output '$relPath' contains an absolute build/source path."
        }
    }
}

foreach ($requiredPath in $required.Keys) {
    if (-not $required[$requiredPath]) {
        Add-Violation "Required file is missing: '$requiredPath'."
    }
}

# --- 5. License and notices tree ------------------------------------------------------------------
$licenseFiles = @($policy.requiredFiles | Where-Object { $_ -like "THIRD-PARTY-LICENSES/*" })
$licenseFiles += @("LICENSE.txt", "NOTICES.md")
foreach ($licenseRel in $licenseFiles) {
    $licensePath = Join-Path $Root $licenseRel
    if (-not (Test-Path $licensePath -PathType Leaf)) {
        Add-Violation "License or notice file is missing: '$licenseRel'."
    } elseif ((Get-Item $licensePath).Length -eq 0) {
        Add-Violation "License or notice file is empty: '$licenseRel'."
    }
}
$licenseDir = Join-Path $Root "THIRD-PARTY-LICENSES"
if (-not (Test-Path $licenseDir -PathType Container)) {
    Add-Violation "The THIRD-PARTY-LICENSES directory is missing from the release tree."
}

# --- 6. Reproducibility -----------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
    if (-not (Test-Path $ZipPath -PathType Leaf)) {
        Add-Violation "Release archive to verify does not exist: '$ZipPath'."
    } else {
        $expectedHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $archiveName = [System.IO.Path]::GetFileName($ZipPath)
        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("srncc-release-repro-" + [Guid]::NewGuid().ToString("N"))
        try {
            $hashes = @()
            foreach ($pass in @("pass1", "pass2")) {
                $passDir = Join-Path $tempRoot $pass
                & "$PSScriptRoot/PackRelease.ps1" -PublishDir $Root -OutputDir $passDir | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Add-Violation "Reproducibility repack '$pass' failed with exit code $LASTEXITCODE."
                    break
                }
                $packed = Join-Path $passDir $archiveName
                if (-not (Test-Path $packed -PathType Leaf)) {
                    Add-Violation "Reproducibility repack '$pass' did not produce '$archiveName'."
                    break
                }
                $hashes += (Get-FileHash -Path $packed -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            if ($hashes.Count -eq 2) {
                if ($hashes[0] -ne $hashes[1]) {
                    Add-Violation "Packing twice produced different archives: '$($hashes[0])' and '$($hashes[1])'."
                } elseif ($hashes[0] -ne $expectedHash) {
                    Add-Violation "Release archive SHA-256 '$expectedHash' is not reproducible; repacking yields '$($hashes[0])'."
                } else {
                    Write-Host "Release archive is reproducible: $expectedHash" -ForegroundColor Green
                }
            }
        } finally {
            if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
        }
    }
}

if ($script:violations -gt 0) {
    Write-Error "Release audit failed with $script:violations violation(s)."
    exit 1
}

Write-Host "Release audit PASSED. Verified $($shipped.Count) shipped file(s) in '$Root'." -ForegroundColor Green
exit 0
