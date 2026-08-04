# tools/PackRelease.ps1
# Deterministic release packer. Produces a byte-reproducible ZIP from a published tree by driving
# System.IO.Compression.ZipArchive directly: Compress-Archive embeds live timestamps and makes no
# guarantee about entry order, so it can never produce a reproducible archive.
[CmdletBinding()]
param(
    [string]$PublishDir = "",
    [string]$OutputDir = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression | Out-Null

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot "artifacts/publish/win-x64"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts/release"
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$releasePolicyPath = Join-Path $repoRoot "eng/release-policy.json"
$publishPolicyPath = Join-Path $repoRoot "eng/publish-policy.json"
$versionsPropsPath = Join-Path $repoRoot "eng/Versions.props"

foreach ($required in @($releasePolicyPath, $publishPolicyPath, $versionsPropsPath)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        Write-Error "Required input is missing: $required"
        exit 1
    }
}
if (-not (Test-Path $PublishDir -PathType Container)) {
    Write-Error "Publish directory does not exist: $PublishDir"
    exit 1
}

$releasePolicy = Get-Content $releasePolicyPath -Raw | ConvertFrom-Json
$publishPolicy = Get-Content $publishPolicyPath -Raw | ConvertFrom-Json

# The product version is single-sourced in eng/Versions.props. Read it by XML rather than
# duplicating the literal anywhere in the release tooling.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionsXml = New-Object System.Xml.XmlDocument
    $versionsXml.PreserveWhitespace = $true
    $versionsXml.Load($versionsPropsPath)
    $versionNode = $versionsXml.SelectSingleNode("//PropertyGroup/SRNCCVersionPrefix")
    if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        Write-Error "eng/Versions.props does not define SRNCCVersionPrefix."
        exit 1
    }
    $Version = $versionNode.InnerText.Trim()
}

$targetRid = $publishPolicy.targetRID
$archiveName = $releasePolicy.releaseNameTemplate.Replace("{version}", $Version).Replace("{rid}", $targetRid)
$archivePath = Join-Path $OutputDir $archiveName
$manifestPath = Join-Path $OutputDir "release-manifest.json"

# Entries excluded from the archive. publish-inventory.json carries a live timestampUtc
# (AuditPublish.ps1:187) and is audit evidence rather than an application file: shipping it would
# make the archive unreproducible forever.
$excluded = @{}
foreach ($name in $releasePolicy.zipExcludedFiles) {
    $excluded[$name.Replace('\', '/').ToLowerInvariant()] = $true
}

$entryTimestampUtc = [DateTime]::Parse(
    $releasePolicy.zipEntryTimestampUtc,
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
# A fixed zero offset, not the local offset: the DOS timestamp ZipArchive writes is derived from the
# offset's local component, so any other offset would make the archive machine-dependent (and below
# the 1980 DOS floor west of UTC).
$entryTimestamp = New-Object System.DateTimeOffset($entryTimestampUtc, [System.TimeSpan]::Zero)

$relativePaths = New-Object 'System.Collections.Generic.List[string]'
$fileByRelativePath = @{}
foreach ($file in (Get-ChildItem -Path $PublishDir -Recurse -File)) {
    $relPath = $file.FullName.Substring($PublishDir.Length).TrimStart('\', '/').Replace('\', '/')
    if ($excluded.ContainsKey($relPath.ToLowerInvariant()) -or $excluded.ContainsKey($file.Name.ToLowerInvariant())) {
        continue
    }
    $relativePaths.Add($relPath)
    $fileByRelativePath[$relPath] = $file
}

if ($relativePaths.Count -eq 0) {
    Write-Error "No files to pack from '$PublishDir'."
    exit 1
}

# Ordinal, not culture-sensitive: Sort-Object would order entries differently under a different
# culture and break reproducibility across machines.
$relativePaths.Sort([System.StringComparer]::Ordinal)

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
if (Test-Path $archivePath) { Remove-Item $archivePath -Force }

Write-Host "Packing $($relativePaths.Count) file(s) from '$PublishDir' into '$archivePath'." -ForegroundColor Cyan

$files = @()
$archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($relPath in $relativePaths) {
            $file = $fileByRelativePath[$relPath]
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)

            # No directory entries: only file entries, with '/' separators.
            $entry = $archive.CreateEntry($relPath, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $entryTimestamp
            $entryStream = $entry.Open()
            try {
                $entryStream.Write($bytes, 0, $bytes.Length)
            } finally {
                $entryStream.Dispose()
            }

            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $hashBytes = $sha.ComputeHash($bytes)
            } finally {
                $sha.Dispose()
            }
            $files += [ordered]@{
                relativePath = $relPath
                sizeBytes = $bytes.LongLength
                sha256 = ([System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant())
            }
        }
    } finally {
        $archive.Dispose()
    }
} finally {
    $archiveStream.Dispose()
}

$archiveSha256 = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    targetRID = $targetRid
    archiveFileName = $archiveName
    archiveSizeBytes = (Get-Item $archivePath).Length
    archiveSha256 = $archiveSha256
    zipEntryTimestampUtc = $releasePolicy.zipEntryTimestampUtc
    fileCount = $files.Count
    files = @($files)
} | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Packed '$archiveName' ($($files.Count) entries)." -ForegroundColor Green
Write-Host "  SHA-256: $archiveSha256" -ForegroundColor Green
Write-Host "  Manifest: $manifestPath" -ForegroundColor Green
exit 0
