# tools/VerifyBuild.ps1
[CmdletBinding()]
param(
    [switch]$GenerateLocks
)

$ErrorActionPreference = "Stop"

# ZipFile.ExtractToDirectory (step [10]) is not loaded by default under Windows PowerShell 5.1.
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$targetRid = "win-x64"
$runtimeVersion = "10.0.10"
$runId = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ") + "-$PID"
$runDir = Join-Path $repoRoot "artifacts/verification-runs/$runId"
$publishDir = Join-Path $runDir "publish/$targetRid"
$releaseDir = Join-Path $runDir "release"
$summaryPath = Join-Path $runDir "summary.json"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

# Populated by step [9]; reported in summary.json on both the success and the failure path, so they
# have to exist before the first Write-VerificationSummary call can be reached.
$script:productVersion = ""
$script:releaseZipPath = ""
$script:releaseZipSha256 = ""

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-VerificationInputHashes {
    $hashes = @{}
    $files = @(
        Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") -Filter "packages.lock.json" -Recurse
        Get-Item (Join-Path $repoRoot "eng/vendored-sources-manifest.json")
        Get-ChildItem -Path (Join-Path $repoRoot "src/SRN.CC.Formats/Vendored"), (Join-Path $repoRoot "tests/SRN.CC.Tests/Vendored") -File -Recurse
    )
    foreach ($file in $files) {
        $hashes[$file.FullName] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    }
    return $hashes
}

function Assert-VerificationInputsUnchanged([hashtable]$Before) {
    $after = Get-VerificationInputHashes
    if ($Before.Count -ne $after.Count) {
        throw "Verification changed the number of lock/vendored input files."
    }
    foreach ($path in $Before.Keys) {
        if (-not $after.ContainsKey($path) -or $after[$path] -ne $Before[$path]) {
            throw "Verification mutated locked/generated input '$path'."
        }
    }
}

# The product version is single-sourced in eng/Versions.props exactly as PackRelease.ps1 reads it.
# Never duplicate the literal here: summary.json's version must be the version the packer used.
function Get-ProductVersion {
    $versionsXml = New-Object System.Xml.XmlDocument
    $versionsXml.Load((Join-Path $repoRoot "eng/Versions.props"))
    $node = $versionsXml.SelectSingleNode("//PropertyGroup/SRNCCVersionPrefix")
    if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "eng/Versions.props does not define SRNCCVersionPrefix."
    }
    return $node.InnerText.Trim()
}

# Package versions come from eng/dependency-policy.json, the file AuditDependencies.ps1 already
# enforces. Hard-coding them here duplicated every version a third time (script, publish-policy's
# specialOrigins, dependency-policy) and made a version bump silently copy the wrong licence.
function Get-ApprovedPackageVersion([object]$Policy, [string]$Id) {
    $package = @($Policy.approvedPackages | Where-Object { $_.id -eq $Id })
    if ($package.Count -ne 1) {
        throw "eng/dependency-policy.json must approve exactly one package with id '$Id'; found $($package.Count)."
    }
    return $package[0].version
}

function Get-ApprovedPackageDirectory([string]$PackagesPath, [object]$Policy, [string]$Id) {
    $version = Get-ApprovedPackageVersion $Policy $Id
    # NuGet's global packages folder stores ids and versions lower-cased.
    return Join-Path $PackagesPath ("{0}/{1}" -f $Id.ToLowerInvariant(), $version.ToLowerInvariant())
}

function Write-VerificationSummary([string]$Status, [string]$SdkVersion, [string]$ErrorMessage = "") {
    $gitStatus = (& git status --porcelain) -join "`n"
    $summary = [ordered]@{
        status = $Status
        timestampUtc = [DateTime]::UtcNow.ToString("o")
        runId = $runId
        sdkVersion = $SdkVersion
        runtimeFrameworkVersion = $runtimeVersion
        configuration = "Release"
        targetRID = $targetRid
        publishDirectory = $publishDir
        version = $script:productVersion
        releaseZipPath = $script:releaseZipPath
        releaseZipSha256 = $script:releaseZipSha256
        gitStatus = $gitStatus
        error = $ErrorMessage
    } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($summaryPath, $summary, [System.Text.Encoding]::UTF8)
}

$sdkVersion = "unknown"
$inputHashes = Get-VerificationInputHashes

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Starting SRN.CC Master Verification and Release Pass " -ForegroundColor Cyan
Write-Host " Run: $runId" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

try {
    $sdkVersion = (dotnet --version).Trim()
    Write-Host "[1/11] Checking SDK version: $sdkVersion..." -ForegroundColor Cyan
    if ($sdkVersion -ne "10.0.302") {
        throw "SDK version mismatch. Expected 10.0.302, found '$sdkVersion'."
    }

    Write-Host "[2/11] Verifying ignore rules and restoring the $targetRid graph..." -ForegroundColor Cyan
    foreach ($ignoredProbe in @("content/__milestone1_probe__.hak", "tools/local_only/__milestone1_probe__.exe", "artifacts/__milestone1_probe__.tmp")) {
        & git check-ignore --quiet --no-index $ignoredProbe
        if ($LASTEXITCODE -ne 0) { throw "Required local-only path is not ignored: $ignoredProbe" }
    }
    $trackedForbidden = @(& git ls-files -- "content" "tools/local_only" "artifacts")
    if ($trackedForbidden.Count -gt 0) {
        throw "Forbidden local-only paths are tracked: $($trackedForbidden -join ', ')"
    }

    if ($GenerateLocks) {
        Invoke-Checked { dotnet restore SRN.CC.sln --force-evaluate -p:SRNCCVerificationRuntimeIdentifier=$targetRid } "Unlocked solution restore"
        $inputHashes = Get-VerificationInputHashes
    } else {
        Invoke-Checked { dotnet restore SRN.CC.sln --locked-mode -p:SRNCCVerificationRuntimeIdentifier=$targetRid } "Locked solution restore"
    }

    Write-Host "[3/11] Running dependency and vendored-source audits..." -ForegroundColor Cyan
    & "$PSScriptRoot/AuditDependencies.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Dependency audit failed with exit code $LASTEXITCODE." }
    & "$PSScriptRoot/AuditVendoredSources.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Vendored-source audit failed with exit code $LASTEXITCODE." }

    Write-Host "[4/11] Building the solution in Release/$targetRid..." -ForegroundColor Cyan
    Invoke-Checked { dotnet build SRN.CC.sln -c Release --no-restore -p:SRNCCVerificationRuntimeIdentifier=$targetRid } "Release build"

    Write-Host "[5/11] Running portable and headless functional tests..." -ForegroundColor Cyan
    Invoke-Checked { dotnet test SRN.CC.sln -c Release --no-build --filter 'Category!=Corpus&Category!=Performance' -p:SRNCCVerificationRuntimeIdentifier=$targetRid --results-directory (Join-Path $runDir "test-results") } "Portable/headless tests"

    Write-Host "[6/11] Publishing a self-contained $targetRid application..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Invoke-Checked { dotnet publish src/SRN.CC.App/SRN.CC.App.csproj -c Release -r $targetRid --self-contained true --no-restore -o $publishDir } "Self-contained publish"
    Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse | Remove-Item -Force

    Write-Host "[7/11] Adding reviewed licenses and notices..." -ForegroundColor Cyan
    $licenseDir = Join-Path $publishDir "THIRD-PARTY-LICENSES"
    New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
    Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $publishDir "LICENSE.txt") -Force
    Copy-Item (Join-Path $repoRoot "NOTICES.md") (Join-Path $publishDir "NOTICES.md") -Force
    Copy-Item (Join-Path $repoRoot "docs/compliance/THIRD-PARTY-LICENSES/SWLOR-LICENSE.txt") (Join-Path $licenseDir "SWLOR-LICENSE.txt") -Force

    $assets = Get-Content (Join-Path $repoRoot "src/SRN.CC.App/obj/project.assets.json") -Raw | ConvertFrom-Json
    $packagesPath = $assets.project.restore.packagesPath
    $dependencyPolicy = Get-Content (Join-Path $repoRoot "eng/dependency-policy.json") -Raw | ConvertFrom-Json
    $runtimePackageDir = Join-Path $packagesPath "microsoft.netcore.app.runtime.$targetRid/$runtimeVersion"
    $anglePackageDir = Get-ApprovedPackageDirectory $packagesPath $dependencyPolicy "Avalonia.Angle.Windows.Natives"
    $skiaPackageDir = Get-ApprovedPackageDirectory $packagesPath $dependencyPolicy "SkiaSharp"
    $harfBuzzPackageDir = Get-ApprovedPackageDirectory $packagesPath $dependencyPolicy "HarfBuzzSharp"
    $toolkitPackageDir = Get-ApprovedPackageDirectory $packagesPath $dependencyPolicy "CommunityToolkit.Mvvm"
    Copy-Item (Join-Path $runtimePackageDir "LICENSE.TXT") (Join-Path $licenseDir "DOTNET-LICENSE.txt") -Force
    Copy-Item (Join-Path $runtimePackageDir "THIRD-PARTY-NOTICES.TXT") (Join-Path $licenseDir "DOTNET-THIRD-PARTY-NOTICES.txt") -Force
    Copy-Item (Join-Path $anglePackageDir "LICENSE") (Join-Path $licenseDir "ANGLE-LICENSE.txt") -Force
    Copy-Item (Join-Path $skiaPackageDir "LICENSE.txt") (Join-Path $licenseDir "SKIASHARP-LICENSE.txt") -Force
    Copy-Item (Join-Path $harfBuzzPackageDir "LICENSE.txt") (Join-Path $licenseDir "HARFBUZZSHARP-LICENSE.txt") -Force
    Copy-Item (Join-Path $toolkitPackageDir "License.md") (Join-Path $licenseDir "COMMUNITYTOOLKIT-LICENSE.md") -Force
    Copy-Item (Join-Path $toolkitPackageDir "ThirdPartyNotices.txt") (Join-Path $licenseDir "COMMUNITYTOOLKIT-THIRD-PARTY-NOTICES.txt") -Force

    Write-Host "[8/11] Auditing every published file and resolved origin..." -ForegroundColor Cyan
    & "$PSScriptRoot/AuditPublish.ps1" -PublishDir $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish audit failed with exit code $LASTEXITCODE." }

    $smokeProcess = $null
    try {
        $smokeProcess = Start-Process -FilePath (Join-Path $publishDir "SRN.CC.App.exe") -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 1500
        if ($smokeProcess.HasExited) {
            throw "Published Release application exited during launch smoke test with code $($smokeProcess.ExitCode)."
        }
    } finally {
        if ($smokeProcess -and -not $smokeProcess.HasExited) {
            Stop-Process -Id $smokeProcess.Id -Force
            $smokeProcess.WaitForExit()
        }
    }

    # [8b] The release gate over the tree step [6] already published. Deliberately not a second
    # publish: auditing a freshly re-published tree would audit something other than the tree that
    # step [9] is about to pack, which is the only tree that matters.
    Write-Host "[8b/11] Auditing the published tree as a release candidate..." -ForegroundColor Cyan
    & "$PSScriptRoot/AuditRelease.ps1" -Root $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Release audit failed with exit code $LASTEXITCODE." }

    # [9] -Version is deliberately left unset so the archive name comes from eng/Versions.props.
    # Packing twice and comparing is the packer-level determinism assertion: PackRelease.ps1 is
    # invoked directly rather than through AuditRelease.ps1 -ZipPath, which would repack twice more
    # for no additional information at roughly thirty seconds a pack.
    Write-Host "[9/11] Packing the release archive twice and comparing SHA-256..." -ForegroundColor Cyan
    $script:productVersion = Get-ProductVersion
    $packProbeDir = Join-Path $runDir "release-determinism-probe"
    & "$PSScriptRoot/PackRelease.ps1" -PublishDir $publishDir -OutputDir $releaseDir
    if ($LASTEXITCODE -ne 0) { throw "Release pack (first) failed with exit code $LASTEXITCODE." }
    & "$PSScriptRoot/PackRelease.ps1" -PublishDir $publishDir -OutputDir $packProbeDir
    if ($LASTEXITCODE -ne 0) { throw "Release pack (second) failed with exit code $LASTEXITCODE." }

    # The archive name comes from eng/release-policy.json's template, the same source PackRelease.ps1
    # uses, so the driver never has to be edited when the naming convention changes.
    $releasePolicy = Get-Content (Join-Path $repoRoot "eng/release-policy.json") -Raw | ConvertFrom-Json
    $archiveName = $releasePolicy.releaseNameTemplate.Replace("{version}", $script:productVersion).Replace("{rid}", $targetRid)
    $archivePath = Join-Path $releaseDir $archiveName
    $probeArchivePath = Join-Path $packProbeDir $archiveName
    foreach ($produced in @($archivePath, $probeArchivePath)) {
        if (-not (Test-Path $produced -PathType Leaf)) { throw "PackRelease.ps1 did not produce '$produced'." }
    }
    $firstHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $secondHash = (Get-FileHash -Path $probeArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  pack 1 SHA-256: $firstHash"
    Write-Host "  pack 2 SHA-256: $secondHash"
    if ($firstHash -ne $secondHash) {
        throw "The release packer is not deterministic: '$firstHash' and '$secondHash'."
    }
    Remove-Item $packProbeDir -Recurse -Force
    $script:releaseZipPath = $archivePath
    $script:releaseZipSha256 = $firstHash

    $manifestPath = Join-Path $releaseDir "release-manifest.json"
    if (-not (Test-Path $manifestPath -PathType Leaf)) { throw "PackRelease.ps1 did not produce '$manifestPath'." }

    # [10] Extract outside the repository: extracting under artifacts/ would put a second copy of
    # every shipped file inside the tree AuditRelease.ps1's PublishSingleFile scan and the
    # forbidden-path checks walk, and would leave it behind in the uploaded evidence artifact.
    Write-Host "[10/11] Extracting the archive outside the repository and auditing the result..." -ForegroundColor Cyan
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("srncc-verify-extract-" + [Guid]::NewGuid().ToString("N"))
    if ($extractRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The extraction scratch directory '$extractRoot' is inside the repository."
    }
    try {
        New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractRoot)

        # Equivalence between the archive and the folder, proven against the packer's own per-file
        # manifest rather than by a third and fourth repack.
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $extracted = @{}
        foreach ($file in (Get-ChildItem -Path $extractRoot -Recurse -File)) {
            $relPath = $file.FullName.Substring($extractRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $extracted[$relPath] = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        if ($extracted.Count -ne $manifest.fileCount) {
            throw "The extracted tree holds $($extracted.Count) file(s) but release-manifest.json records $($manifest.fileCount)."
        }
        foreach ($entry in $manifest.files) {
            if (-not $extracted.ContainsKey($entry.relativePath)) {
                throw "The extracted tree is missing manifest entry '$($entry.relativePath)'."
            }
            if ($extracted[$entry.relativePath] -ne $entry.sha256) {
                throw "Extracted '$($entry.relativePath)' hashes '$($extracted[$entry.relativePath])', manifest records '$($entry.sha256)'."
            }
        }
        Write-Host "  Extracted tree matches all $($manifest.fileCount) manifest entries." -ForegroundColor Green

        & "$PSScriptRoot/AuditRelease.ps1" -Root $extractRoot
        if ($LASTEXITCODE -ne 0) { throw "Release audit of the extracted archive failed with exit code $LASTEXITCODE." }
    } finally {
        if (Test-Path $extractRoot) { Remove-Item $extractRoot -Recurse -Force }
    }

    Write-Host "[11/11] Confirming locked and vendored inputs did not change..." -ForegroundColor Cyan
    Assert-VerificationInputsUnchanged $inputHashes

    Write-VerificationSummary "SUCCESS" $sdkVersion
    Write-Host "Verification PASSED. Evidence: $runDir" -ForegroundColor Green
    exit 0
} catch {
    Write-VerificationSummary "FAILED" $sdkVersion $_.Exception.Message
    Write-Error "Verification FAILED: $($_.Exception.Message). Evidence: $runDir"
    exit 1
}
