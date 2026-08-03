# tools/VerifyBuild.ps1
[CmdletBinding()]
param(
    [switch]$GenerateLocks
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$targetRid = "win-x64"
$runtimeVersion = "10.0.10"
$runId = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ") + "-$PID"
$runDir = Join-Path $repoRoot "artifacts/verification-runs/$runId"
$publishDir = Join-Path $runDir "publish/$targetRid"
$summaryPath = Join-Path $runDir "summary.json"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

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
        gitStatus = $gitStatus
        error = $ErrorMessage
    } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($summaryPath, $summary, [System.Text.Encoding]::UTF8)
}

$sdkVersion = "unknown"
$inputHashes = Get-VerificationInputHashes

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Starting SRN.CC Milestones 1-3 Master Verification Pass " -ForegroundColor Cyan
Write-Host " Run: $runId" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

try {
    $sdkVersion = (dotnet --version).Trim()
    Write-Host "[1/9] Checking SDK version: $sdkVersion..." -ForegroundColor Cyan
    if ($sdkVersion -ne "10.0.302") {
        throw "SDK version mismatch. Expected 10.0.302, found '$sdkVersion'."
    }

    Write-Host "[2/9] Verifying ignore rules and restoring the $targetRid graph..." -ForegroundColor Cyan
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

    Write-Host "[3/9] Running dependency and vendored-source audits..." -ForegroundColor Cyan
    & "$PSScriptRoot/AuditDependencies.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Dependency audit failed with exit code $LASTEXITCODE." }
    & "$PSScriptRoot/AuditVendoredSources.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Vendored-source audit failed with exit code $LASTEXITCODE." }

    Write-Host "[4/9] Building the solution in Release/$targetRid..." -ForegroundColor Cyan
    Invoke-Checked { dotnet build SRN.CC.sln -c Release --no-restore -p:SRNCCVerificationRuntimeIdentifier=$targetRid } "Release build"

    Write-Host "[5/9] Running portable and headless functional tests..." -ForegroundColor Cyan
    Invoke-Checked { dotnet test SRN.CC.sln -c Release --no-build --filter 'Category!=Corpus&Category!=Performance' -p:SRNCCVerificationRuntimeIdentifier=$targetRid --results-directory (Join-Path $runDir "test-results") } "Portable/headless tests"

    Write-Host "[6/9] Publishing a self-contained $targetRid application..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Invoke-Checked { dotnet publish src/SRN.CC.App/SRN.CC.App.csproj -c Release -r $targetRid --self-contained true --no-restore -o $publishDir } "Self-contained publish"
    Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse | Remove-Item -Force

    Write-Host "[7/9] Adding reviewed licenses and notices..." -ForegroundColor Cyan
    $licenseDir = Join-Path $publishDir "THIRD-PARTY-LICENSES"
    New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
    Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $publishDir "LICENSE.txt") -Force
    Copy-Item (Join-Path $repoRoot "NOTICES.md") (Join-Path $publishDir "NOTICES.md") -Force
    Copy-Item (Join-Path $repoRoot "docs/compliance/THIRD-PARTY-LICENSES/SWLOR-LICENSE.txt") (Join-Path $licenseDir "SWLOR-LICENSE.txt") -Force

    $assets = Get-Content (Join-Path $repoRoot "src/SRN.CC.App/obj/project.assets.json") -Raw | ConvertFrom-Json
    $packagesPath = $assets.project.restore.packagesPath
    $runtimePackageDir = Join-Path $packagesPath "microsoft.netcore.app.runtime.$targetRid/$runtimeVersion"
    $anglePackageDir = Join-Path $packagesPath "avalonia.angle.windows.natives/2.1.27548.20260419"
    $skiaPackageDir = Join-Path $packagesPath "skiasharp/3.119.4"
    $harfBuzzPackageDir = Join-Path $packagesPath "harfbuzzsharp/8.3.1.3"
    $toolkitPackageDir = Join-Path $packagesPath "communitytoolkit.mvvm/8.4.2"
    Copy-Item (Join-Path $runtimePackageDir "LICENSE.TXT") (Join-Path $licenseDir "DOTNET-LICENSE.txt") -Force
    Copy-Item (Join-Path $runtimePackageDir "THIRD-PARTY-NOTICES.TXT") (Join-Path $licenseDir "DOTNET-THIRD-PARTY-NOTICES.txt") -Force
    Copy-Item (Join-Path $anglePackageDir "LICENSE") (Join-Path $licenseDir "ANGLE-LICENSE.txt") -Force
    Copy-Item (Join-Path $skiaPackageDir "LICENSE.txt") (Join-Path $licenseDir "SKIASHARP-LICENSE.txt") -Force
    Copy-Item (Join-Path $harfBuzzPackageDir "LICENSE.txt") (Join-Path $licenseDir "HARFBUZZSHARP-LICENSE.txt") -Force
    Copy-Item (Join-Path $toolkitPackageDir "License.md") (Join-Path $licenseDir "COMMUNITYTOOLKIT-LICENSE.md") -Force
    Copy-Item (Join-Path $toolkitPackageDir "ThirdPartyNotices.txt") (Join-Path $licenseDir "COMMUNITYTOOLKIT-THIRD-PARTY-NOTICES.txt") -Force

    Write-Host "[8/9] Auditing every published file and resolved origin..." -ForegroundColor Cyan
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

    Write-Host "[9/9] Confirming locked and vendored inputs did not change..." -ForegroundColor Cyan
    Assert-VerificationInputsUnchanged $inputHashes

    Write-VerificationSummary "SUCCESS" $sdkVersion
    Write-Host "Verification PASSED. Evidence: $runDir" -ForegroundColor Green
    exit 0
} catch {
    Write-VerificationSummary "FAILED" $sdkVersion $_.Exception.Message
    Write-Error "Verification FAILED: $($_.Exception.Message). Evidence: $runDir"
    exit 1
}
