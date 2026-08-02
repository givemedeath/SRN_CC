# tools/VerifyBuild.ps1
[CmdletBinding()]
param(
    [switch]$GenerateLocks
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Starting SRN.CC Milestone 1 Master Verification Pass " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Verify SDK Version
$sdkVersion = (dotnet --version).Trim()
Write-Host "[1/9] Checking SDK Version: $sdkVersion..." -ForegroundColor Cyan
if ($sdkVersion -ne "10.0.302") {
    Write-Error "SDK version mismatch! Expected 10.0.302, found '$sdkVersion'."
    exit 1
}

# 2. Restore Solution
Write-Host "[2/9] Restoring packages..." -ForegroundColor Cyan
if ($GenerateLocks) {
    dotnet restore SRN.CC.sln
    dotnet restore src/SRN.CC.App/SRN.CC.App.csproj -r win-x64
} else {
    dotnet restore SRN.CC.sln --locked-mode
    dotnet restore src/SRN.CC.App/SRN.CC.App.csproj -r win-x64 --locked-mode
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet restore failed with exit code $LASTEXITCODE."
    exit 1
}

# 3. Audit Dependencies & Vendored Sources
Write-Host "[3/9] Running dependency and vendored sources compliance audits..." -ForegroundColor Cyan
& "$PSScriptRoot/AuditDependencies.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& "$PSScriptRoot/AuditVendoredSources.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. Build Solution (Release)
Write-Host "[4/9] Building SRN.CC solution in Release configuration..." -ForegroundColor Cyan
dotnet build SRN.CC.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Release build failed with exit code $LASTEXITCODE."
    exit 1
}

# 5. Run Portable and Headless Tests
Write-Host "[5/9] Running portable unit and headless UI tests..." -ForegroundColor Cyan
dotnet test SRN.CC.sln -c Release --no-build --filter "Category!=Corpus&Category!=Performance"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test suite failed with exit code $LASTEXITCODE."
    exit 1
}

# 6. Publish Self-Contained App for win-x64
$publishDir = Join-Path $repoRoot "artifacts/publish/win-x64"
if (Test-Path $publishDir) { Remove-Item -Path $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "[6/9] Publishing self-contained Release App for win-x64..." -ForegroundColor Cyan
dotnet publish src/SRN.CC.App/SRN.CC.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE."
    exit 1
}

# 7. Copy NOTICES.md to Publish Directory
Copy-Item -Path "$repoRoot/NOTICES.md" -Destination "$publishDir/NOTICES.md" -Force
Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse | Remove-Item -Force

# 8. Audit Publish Output
Write-Host "[7/9] Running publish compliance audit..." -ForegroundColor Cyan
& "$PSScriptRoot/AuditPublish.ps1" -PublishDir $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 9. Verify Repository State
Write-Host "[8/9] Verifying Git status..." -ForegroundColor Cyan
$gitStatus = git status --porcelain
if ([string]::IsNullOrWhiteSpace($gitStatus)) {
    Write-Host "Git workspace is completely clean." -ForegroundColor Green
} else {
    Write-Host "Untracked or modified files in git status:" -ForegroundColor Yellow
    Write-Host $gitStatus
}

# Write Verification Summary
$summaryDir = Join-Path $repoRoot "artifacts/verification"
New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null

$summary = @{
    status = "SUCCESS"
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    sdkVersion = $sdkVersion
    configuration = "Release"
    targetRID = "win-x64"
    verificationSteps = 9
} | ConvertTo-Json -Depth 3

[System.IO.File]::WriteAllText("$summaryDir/summary.json", $summary, [System.Text.Encoding]::UTF8)

Write-Host "============================================================" -ForegroundColor Green
Write-Host " Milestone 1 Master Verification Pass Completed SUCCESSFULLY! " -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
