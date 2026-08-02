# tools/AuditVendoredSources.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot "eng/vendored-sources-manifest.json"

if (-not (Test-Path $manifestPath)) {
    Write-Error "Vendored sources manifest not found: $manifestPath"
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
Write-Host "Auditing vendored sources from commit $($manifest.commit)..." -ForegroundColor Cyan

$violations = 0
foreach ($entry in $manifest.entries) {
    if ($entry.status -eq "verbatim" -or $entry.status -eq "adapted") {
        $destPath = Join-Path $repoRoot $entry.destinationPath
        if (-not (Test-Path $destPath)) {
            Write-Host "VENDOR AUDIT ERROR: Destination file missing: $destPath" -ForegroundColor Red
            $violations++
            continue
        }

        $actualHash = (Get-FileHash -Path $destPath -Algorithm SHA256).Hash.ToLower()
        if ($actualHash -ne $entry.sha256.ToLower()) {
            Write-Host "VENDOR AUDIT ERROR: Hash mismatch for $($entry.destinationPath). Expected $($entry.sha256), got $actualHash" -ForegroundColor Red
            $violations++
        }
    }
}

# Scan for forbidden Radoub / GPL identifiers
$forbiddenPatterns = @("Radoub", "GPL")
$vendoredTree = Join-Path $repoRoot "src/SRN.CC.Formats/Vendored"
if (Test-Path $vendoredTree) {
    $codeFiles = Get-ChildItem -Path $vendoredTree -Recurse -Include *.cs,*.md
    foreach ($f in $codeFiles) {
        $text = Get-Content $f.FullName -Raw
        foreach ($pattern in $forbiddenPatterns) {
            if ($text -match "\b$pattern\b") {
                Write-Host "VENDOR AUDIT ERROR: Forbidden pattern '$pattern' found in file $($f.FullName)" -ForegroundColor Red
                $violations++
            }
        }
    }
}

if ($violations -gt 0) {
    Write-Error "Vendored sources audit failed with $violations violation(s)."
    exit 1
}

Write-Host "Vendored sources audit PASSED. All $($manifest.entries.Count) entries verified." -ForegroundColor Green
exit 0
