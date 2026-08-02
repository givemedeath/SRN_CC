# tools/AuditDependencies.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$policyPath = Join-Path $repoRoot "eng/dependency-policy.json"

if (-not (Test-Path $policyPath)) {
    Write-Error "Dependency policy file not found: $policyPath"
    exit 1
}

$policy = Get-Content $policyPath -Raw | ConvertFrom-Json
$approvedMap = @{}
foreach ($pkg in $policy.approvedPackages) {
    $approvedMap[$pkg.id.ToLower()] = $pkg
}

Write-Host "Auditing dependency graph against eng/dependency-policy.json..." -ForegroundColor Cyan

$lockFiles = Get-ChildItem -Path $repoRoot -Filter "packages.lock.json" -Recurse

if ($lockFiles.Count -eq 0) {
    Write-Host "Warning: No packages.lock.json files found. Run restore first." -ForegroundColor Yellow
}

$violations = 0
foreach ($file in $lockFiles) {
    $json = Get-Content $file.FullName -Raw | ConvertFrom-Json
    foreach ($targetProp in $json.PSObject.Properties) {
        if ($targetProp.Name.StartsWith(".NETFramework") -or $targetProp.Name.StartsWith("net10.0")) {
            $pkgs = $targetProp.Value
            foreach ($pkgProp in $pkgs.PSObject.Properties) {
                $pkgId = $pkgProp.Name
                $pkgDetail = $pkgProp.Value
                $resolvedVer = $pkgDetail.resolved

                $key = $pkgId.ToLower()
                if (-not $approvedMap.ContainsKey($key)) {
                    Write-Host "DEPENDENCY VIOLATION: Unapproved package '$pkgId' resolved version '$resolvedVer' in $($file.FullName)" -ForegroundColor Red
                    $violations++
                } else {
                    $approvedVer = $approvedMap[$key].version
                    if ($approvedVer -ne $resolvedVer) {
                        Write-Host "DEPENDENCY VIOLATION: Package '$pkgId' resolved version '$resolvedVer' does not match policy version '$approvedVer'" -ForegroundColor Red
                        $violations++
                    }
                }
            }
        }
    }
}

if ($violations -gt 0) {
    Write-Error "Dependency audit failed with $violations violation(s)."
    exit 1
}

Write-Host "Dependency audit PASSED. All resolved packages match eng/dependency-policy.json." -ForegroundColor Green
exit 0
