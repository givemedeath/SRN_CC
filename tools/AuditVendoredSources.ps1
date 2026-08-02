# tools/AuditVendoredSources.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot "eng/vendored-sources-manifest.json"
$expectedRepository = "https://github.com/givemedeath/SWLOR_NWN"
$expectedCommit = "8202faa203eddd6f4972104d22ea5740e23f20f7"
$script:violations = 0

function Add-Violation([string]$Message) {
    Write-Host "VENDOR AUDIT ERROR: $Message" -ForegroundColor Red
    $script:violations++
}

if (-not (Test-Path $manifestPath)) {
    Write-Error "Vendored sources manifest not found: $manifestPath"
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.repositoryUrl -ne $expectedRepository -or $manifest.commit -ne $expectedCommit) {
    Add-Violation "Manifest repository/commit pin changed from the reviewed SWLOR snapshot."
}

$corpusExclusion = $manifest.excludedTrees | Where-Object { $_.upstreamPath -eq "SWLOR.NWN.Formats.Corpus.Tests/" -and -not [string]::IsNullOrWhiteSpace($_.reason) }
if (-not $corpusExclusion) {
    Add-Violation "The licensed SWLOR corpus-test tree must be explicitly excluded with a reason."
}

Write-Host "Auditing vendored sources from commit $($manifest.commit)..." -ForegroundColor Cyan

$destinationPaths = @{}
$upstreamPaths = @{}
$formatSources = 0
$portableTests = 0
$verbatimCount = 0

foreach ($entry in $manifest.entries) {
    if ([string]::IsNullOrWhiteSpace($entry.upstreamPath) -or $upstreamPaths.ContainsKey($entry.upstreamPath)) {
        Add-Violation "Manifest has a missing or duplicate upstream path '$($entry.upstreamPath)'."
    } else {
        $upstreamPaths[$entry.upstreamPath] = $true
    }

    if ($entry.status -notin @("verbatim", "adapted", "excluded")) {
        Add-Violation "Unsupported status '$($entry.status)' for '$($entry.upstreamPath)'."
        continue
    }

    if ($entry.status -eq "excluded") {
        if ([string]::IsNullOrWhiteSpace($entry.gitBlobSha1) -or [string]::IsNullOrWhiteSpace($entry.exclusionReason)) {
            Add-Violation "Excluded item '$($entry.upstreamPath)' lacks a blob ID or exclusion reason."
        }
        continue
    }

    if ([string]::IsNullOrWhiteSpace($entry.destinationPath) -or $destinationPaths.ContainsKey($entry.destinationPath)) {
        Add-Violation "Manifest has a missing or duplicate destination '$($entry.destinationPath)'."
        continue
    }
    $destinationPaths[$entry.destinationPath] = $true

    if ($entry.status -eq "adapted" -and ([string]::IsNullOrWhiteSpace($entry.adaptationReason) -or [string]::IsNullOrWhiteSpace($entry.reviewReference))) {
        Add-Violation "Adapted item '$($entry.upstreamPath)' lacks its reason or review reference."
    }

    $destPath = Join-Path $repoRoot $entry.destinationPath
    if (-not (Test-Path $destPath -PathType Leaf)) {
        Add-Violation "Destination file missing: $($entry.destinationPath)."
        continue
    }

    $actualHash = (Get-FileHash -Path $destPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.sha256.ToLowerInvariant()) {
        Add-Violation "SHA-256 mismatch for '$($entry.destinationPath)'."
    }

    if ($entry.status -eq "verbatim") {
        $verbatimCount++
        $actualBlob = (& git -C $repoRoot hash-object -- $destPath).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualBlob -ne $entry.gitBlobSha1) {
            Add-Violation "Git blob mismatch for verbatim file '$($entry.destinationPath)'."
        }
    }

    if ($entry.destinationPath -like "src/SRN.CC.Formats/Vendored/*.cs") {
        $formatSources++
    }
    if ($entry.destinationPath -like "tests/SRN.CC.Tests/Vendored/*.cs") {
        $portableTests++
    }
}

if ($formatSources -ne 40 -or $portableTests -ne 8) {
    Add-Violation "Expected 40 vendored format sources and 8 portable test files; found $formatSources and $portableTests."
}

$moduleLock = $manifest.entries | Where-Object { $_.upstreamPath -eq "SWLOR.NWN.Formats/Common/ModuleWriteLock.cs" -and $_.status -eq "excluded" }
if (-not $moduleLock) {
    Add-Violation "ModuleWriteLock.cs must remain explicitly excluded."
}

$scannedFiles = Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") -Recurse -File -Include *.cs,*.csproj,*.axaml
foreach ($file in $scannedFiles) {
    $text = Get-Content $file.FullName -Raw
    foreach ($pattern in @("Radoub", "GPL")) {
        if ($text -match "\b$pattern\b") {
            Add-Violation "Forbidden identifier '$pattern' found in production/test file '$($file.FullName)'."
        }
    }
}

if ($script:violations -gt 0) {
    Write-Error "Vendored sources audit failed with $script:violations violation(s)."
    exit 1
}

Write-Host "Vendored sources audit PASSED. Verified $verbatimCount verbatim blobs, 40 source files, 8 portable test files, and explicit exclusions." -ForegroundColor Green
exit 0
