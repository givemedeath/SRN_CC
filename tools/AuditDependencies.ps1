# tools/AuditDependencies.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$policyPath = Join-Path $repoRoot "eng/dependency-policy.json"
$nugetConfigPath = Join-Path $repoRoot "NuGet.Config"
$script:violations = 0

function Add-Violation([string]$Message) {
    Write-Host "DEPENDENCY VIOLATION: $Message" -ForegroundColor Red
    $script:violations++
}

function Get-RelativeRepoPath([string]$Path) {
    $rootPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Violation "Path '$fullPath' escapes the repository root."
        return $fullPath.Replace('\', '/')
    }

    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

if (-not (Test-Path $policyPath)) {
    Write-Error "Dependency policy file not found: $policyPath"
    exit 1
}

$policy = Get-Content $policyPath -Raw | ConvertFrom-Json
$approvedMap = @{}
foreach ($pkg in $policy.approvedPackages) {
    $key = "$($pkg.id.ToLowerInvariant())@$($pkg.version)"
    if ($approvedMap.ContainsKey($key)) {
        Add-Violation "Duplicate policy entry '$key'."
        continue
    }

    if ($pkg.scope -notin @("runtime", "development", "test")) {
        Add-Violation "Package '$key' has invalid scope '$($pkg.scope)'."
    }
    if ($pkg.scope -eq "runtime" -and $pkg.mustShipNotice -ne $true) {
        Add-Violation "Runtime package '$key' must be marked for distributed notice review."
    }

    $reviewDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact($pkg.reviewDate, "yyyy-MM-dd", $null, [Globalization.DateTimeStyles]::None, [ref]$reviewDate) -or $reviewDate.Date -gt [DateTime]::UtcNow.Date) {
        Add-Violation "Package '$key' has a missing, invalid, or future review date."
    }

    if (-not [Uri]::IsWellFormedUriString($pkg.repositoryUrl, [UriKind]::Absolute)) {
        Add-Violation "Package '$key' has invalid repository/license evidence URL '$($pkg.repositoryUrl)'."
    }

    if ([string]::IsNullOrWhiteSpace($pkg.contentHash)) {
        Add-Violation "Package '$key' has no reviewed lock-file content hash."
    }

    $approvedMap[$key] = $pkg
}

[xml]$nugetConfig = Get-Content $nugetConfigPath -Raw
$signatureMode = $nugetConfig.configuration.config.add | Where-Object { $_.key -eq "signatureValidationMode" } | Select-Object -ExpandProperty value -First 1
if ($signatureMode -ne "require") {
    Add-Violation "NuGet.Config must set signatureValidationMode=require."
}

$lockFiles = @(
    Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") -Filter "packages.lock.json" -Recurse
)
if ($lockFiles.Count -ne 7) {
    Add-Violation "Expected exactly seven project lock files; found $($lockFiles.Count)."
}

Write-Host "Auditing $($lockFiles.Count) lock files against $($approvedMap.Count) package reviews..." -ForegroundColor Cyan

$actualMap = @{}
$runtimeGraph = @{}
foreach ($file in $lockFiles) {
    $json = Get-Content $file.FullName -Raw | ConvertFrom-Json
    if ($json.version -ne 2 -or -not $json.dependencies) {
        Add-Violation "Unsupported or malformed lock file '$($file.FullName)'."
        continue
    }

    $isRuntimeRoot = (Get-RelativeRepoPath $file.FullName) -eq "src/SRN.CC.App/packages.lock.json"
    foreach ($targetProp in $json.dependencies.PSObject.Properties) {
        if ($targetProp.Name -notmatch '^net10\.0(?:/win-x64)?$') {
            Add-Violation "Unexpected lock target '$($targetProp.Name)' in '$(Get-RelativeRepoPath $file.FullName)'."
        }

        foreach ($pkgProp in $targetProp.Value.PSObject.Properties) {
            $pkg = $pkgProp.Value
            if ($pkg.type -eq "Project") {
                continue
            }

            $key = "$($pkgProp.Name.ToLowerInvariant())@$($pkg.resolved)"
            if (-not $approvedMap.ContainsKey($key)) {
                Add-Violation "Unreviewed package '$key' in '$(Get-RelativeRepoPath $file.FullName)'."
                continue
            }

            $approved = $approvedMap[$key]
            if ($approved.contentHash -ne $pkg.contentHash) {
                Add-Violation "Content hash changed for '$key'."
            }

            $actualMap[$key] = $true
            if ($isRuntimeRoot) {
                $runtimeGraph[$key] = $true
                if ($approved.scope -eq "test") {
                    Add-Violation "Runtime graph package '$key' is incorrectly scoped as test-only."
                }
            }
        }
    }
}

foreach ($key in $approvedMap.Keys) {
    if (-not $actualMap.ContainsKey($key)) {
        Add-Violation "Policy contains unused package review '$key'."
    }
}

$assetsFiles = @(
    Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") -Filter "project.assets.json" -Recurse |
        Where-Object { $_.Directory.Name -eq "obj" }
)
if ($assetsFiles.Count -ne 7) {
    Add-Violation "Expected seven restored project.assets.json files; found $($assetsFiles.Count)."
}

$allowedSources = @($policy.allowedPackageSources | ForEach-Object { $_.TrimEnd('/') })
$packagesPath = $null
foreach ($file in $assetsFiles) {
    $assets = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $restore = $assets.project.restore
    if (-not $packagesPath) {
        $packagesPath = $restore.packagesPath
    }

    $audit = $restore.restoreAuditProperties
    if ($audit.enableAudit -ne "true" -or $audit.auditMode -ne "all" -or $audit.auditLevel -ne "low") {
        Add-Violation "NuGet vulnerability audit is not enableAudit=true, auditMode=all, auditLevel=low for '$($restore.projectName)'."
    }

    foreach ($source in $restore.sources.PSObject.Properties.Name) {
        if ($source.TrimEnd('/') -notin $allowedSources) {
            Add-Violation "Unapproved package source '$source' in '$($restore.projectName)'."
        }
    }

    foreach ($log in @($assets.logs)) {
        if ($log.code -match '^NU190[1-4]$') {
            Add-Violation "Vulnerable dependency reported for '$($restore.projectName)': $($log.message)"
        }
    }

    $projectPath = Get-RelativeRepoPath $restore.projectPath
    $actualReferences = @(@(
        foreach ($framework in $restore.frameworks.PSObject.Properties) {
            foreach ($reference in $framework.Value.projectReferences.PSObject.Properties.Name) {
                Get-RelativeRepoPath $reference
            }
        }
    ) | Sort-Object -Unique)
    $expectedProperty = $policy.approvedProjectReferences.PSObject.Properties | Where-Object Name -eq $projectPath
    if (-not $expectedProperty) {
        Add-Violation "No project-reference policy exists for '$projectPath'."
    } else {
        $expectedReferences = @(@($expectedProperty.Value) | Sort-Object -Unique)
        if (Compare-Object $expectedReferences $actualReferences) {
            Add-Violation "Project references for '$projectPath' do not match policy. Expected [$($expectedReferences -join ', ')], actual [$($actualReferences -join ', ')]."
        }
    }
}

if (-not $packagesPath -or -not (Test-Path $packagesPath)) {
    Add-Violation "The restored NuGet package cache path is unavailable."
} else {
    foreach ($key in $actualMap.Keys) {
        $approved = $approvedMap[$key]
        $packageDir = Join-Path $packagesPath (Join-Path $approved.id.ToLowerInvariant() $approved.version.ToLowerInvariant())
        $nuspec = Get-ChildItem $packageDir -Filter "*.nuspec" -File | Select-Object -First 1
        if (-not $nuspec) {
            Add-Violation "NuGet metadata is missing from the cache for '$key'."
            continue
        }

        if (-not (Test-Path (Join-Path $packageDir ".signature.p7s"))) {
            Add-Violation "Restored package '$key' has no repository/author signature."
        }

        [xml]$nuspecXml = Get-Content $nuspec.FullName -Raw
        $metadata = $nuspecXml.package.metadata
        $evidenceUrl = if ($metadata.repository.url) { [string]$metadata.repository.url } else { [string]$metadata.projectUrl }
        if ($evidenceUrl.TrimEnd('/') -ne $approved.repositoryUrl.TrimEnd('/')) {
            Add-Violation "Repository/license evidence changed for '$key'. Expected '$($approved.repositoryUrl)', got '$evidenceUrl'."
        }

        $licenseType = [string]$metadata.license.type
        $licenseValue = [string]$metadata.license.InnerText
        if ($approved.license.type -eq "expression") {
            if ($licenseType -ne "expression" -or $licenseValue -ne $approved.license.value) {
                Add-Violation "License metadata changed for '$key'. Expected expression '$($approved.license.value)', got '${licenseType}:$licenseValue'."
            }
        } elseif ($approved.license.type -eq "file") {
            $licensePath = Join-Path $packageDir $approved.license.file
            if ($licenseType -ne "file" -or $licenseValue -ne $approved.license.file -or -not (Test-Path $licensePath)) {
                Add-Violation "Reviewed license file is missing or changed for '$key'."
            } else {
                $licenseHash = (Get-FileHash $licensePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($licenseHash -ne $approved.license.sha256.ToLowerInvariant()) {
                    Add-Violation "Reviewed license-file hash changed for '$key'."
                }
            }
        } else {
            Add-Violation "Package '$key' has unsupported license review type '$($approved.license.type)'."
        }
    }
}

if ($script:violations -gt 0) {
    Write-Error "Dependency audit failed with $script:violations violation(s)."
    exit 1
}

Write-Host "Dependency audit PASSED. Verified $($actualMap.Count) direct/transitive package versions, metadata, hashes, signatures, sources, audit results, and project references." -ForegroundColor Green
exit 0
