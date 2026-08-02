# tools/AuditPublish.ps1
[CmdletBinding()]
param(
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot "artifacts/publish/win-x64"
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

$policyPath = Join-Path $repoRoot "eng/publish-policy.json"
$dependencyPolicyPath = Join-Path $repoRoot "eng/dependency-policy.json"
$script:violations = 0

function Add-Violation([string]$Message) {
    Write-Host "PUBLISH AUDIT ERROR: $Message" -ForegroundColor Red
    $script:violations++
}

function Add-Origin([hashtable]$Map, [string]$RelativePath, [string]$Origin) {
    $normalized = $RelativePath.Replace('\', '/')
    if ($Map.ContainsKey($normalized) -and $Map[$normalized] -ne $Origin) {
        Add-Violation "Published path '$normalized' has ambiguous origins '$($Map[$normalized])' and '$Origin'."
    } else {
        $Map[$normalized] = $Origin
    }
}

if (-not (Test-Path $policyPath) -or -not (Test-Path $dependencyPolicyPath)) {
    Write-Error "Publish or dependency policy is missing."
    exit 1
}
if (-not (Test-Path $PublishDir -PathType Container)) {
    Write-Error "Publish directory does not exist: $PublishDir"
    exit 1
}

$policy = Get-Content $policyPath -Raw | ConvertFrom-Json
$dependencyPolicy = Get-Content $dependencyPolicyPath -Raw | ConvertFrom-Json
$approvedPackages = @{}
foreach ($package in $dependencyPolicy.approvedPackages) {
    $approvedPackages["$($package.id.ToLowerInvariant())@$($package.version)"] = $package
}

$depsPath = Join-Path $PublishDir $policy.depsFile
if (-not (Test-Path $depsPath -PathType Leaf)) {
    Write-Error "Published deps manifest is missing: $depsPath"
    exit 1
}

$deps = Get-Content $depsPath -Raw | ConvertFrom-Json
$expectedTarget = ".NETCoreApp,Version=v10.0/$($policy.targetRID)"
if ($deps.runtimeTarget.name -ne $expectedTarget) {
    Add-Violation "Expected deps runtime target '$expectedTarget', got '$($deps.runtimeTarget.name)'."
}

$target = $deps.targets.PSObject.Properties | Where-Object Name -eq $deps.runtimeTarget.name | Select-Object -First 1
if (-not $target) {
    Add-Violation "The deps manifest has no resolved target '$($deps.runtimeTarget.name)'."
}

$originMap = @{}
foreach ($special in $policy.specialOrigins.PSObject.Properties) {
    Add-Origin $originMap $special.Name $special.Value
}

if ($target) {
    foreach ($library in $target.Value.PSObject.Properties) {
        $parts = $library.Name.Split('/', 2)
        $libraryId = $parts[0]
        $libraryVersion = if ($parts.Count -eq 2) { $parts[1] } else { "" }
        $libraryMetadata = $deps.libraries.PSObject.Properties | Where-Object Name -eq $library.Name | Select-Object -First 1
        $libraryType = $libraryMetadata.Value.type

        if ($libraryType -eq "package") {
            $packageKey = "$($libraryId.ToLowerInvariant())@$libraryVersion"
            if (-not $approvedPackages.ContainsKey($packageKey)) {
                Add-Violation "Deps manifest contains unreviewed package '$($library.Name)'."
            } elseif ($approvedPackages[$packageKey].scope -ne "runtime") {
                Add-Violation "Deps manifest distributes non-runtime package '$($library.Name)'."
            }
            $origin = "nuget:$($library.Name)"
        } elseif ($libraryType -eq "project") {
            if ($libraryId -notlike "SRN.CC.*") {
                Add-Violation "Deps manifest contains unexpected project '$($library.Name)'."
            }
            $origin = "project:$libraryId"
        } elseif ($libraryType -eq "runtimepack") {
            if ($libraryId -ne "runtimepack.Microsoft.NETCore.App.Runtime.$($policy.targetRID)" -or $libraryVersion -ne $policy.runtimeFrameworkVersion) {
                Add-Violation "Unexpected runtime pack '$($library.Name)'."
            }
            $origin = "runtimepack:$($library.Name)"
        } else {
            Add-Violation "Unknown deps library type '$libraryType' for '$($library.Name)'."
            $origin = "unknown:$($library.Name)"
        }

        foreach ($assetGroup in @("runtime", "native", "resources")) {
            $assets = $library.Value.PSObject.Properties | Where-Object Name -eq $assetGroup | Select-Object -First 1
            foreach ($asset in $assets.Value.PSObject.Properties) {
                Add-Origin $originMap ([System.IO.Path]::GetFileName($asset.Name)) $origin
            }
        }

        $runtimeTargets = $library.Value.PSObject.Properties | Where-Object Name -eq "runtimeTargets" | Select-Object -First 1
        foreach ($asset in $runtimeTargets.Value.PSObject.Properties) {
            if ($asset.Value.rid -and $asset.Value.rid -ne $policy.targetRID) {
                continue
            }
            Add-Origin $originMap ([System.IO.Path]::GetFileName($asset.Name)) $origin
        }
    }
}

Write-Host "Auditing publish directory and resolved origins: $PublishDir" -ForegroundColor Cyan

$required = @{}
foreach ($path in $policy.requiredFiles) { $required[$path.Replace('\', '/')] = $false }
$forbiddenExtensions = @($policy.forbiddenExtensions | ForEach-Object { $_.ToLowerInvariant() })
$forbiddenAssemblies = @($policy.forbiddenAssemblies | ForEach-Object { $_.ToLowerInvariant() })
$inventory = @()

$publishedFiles = Get-ChildItem -Path $PublishDir -Recurse -File | Where-Object { $_.Name -ne "publish-inventory.json" }
foreach ($file in $publishedFiles) {
    $relPath = $file.FullName.Substring($PublishDir.Length).TrimStart('\', '/').Replace('\', '/')
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
        if (($origin.StartsWith("project:") -or $origin.StartsWith("build:")) -and $ascii -match '[A-Za-z]:\\') {
            Add-Violation "First-party output '$relPath' contains an absolute build/source path."
        }
    }

    $inventory += [ordered]@{
        relativePath = $relPath
        sizeBytes = $file.Length
        sha256 = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        origin = $origin
    }
}

foreach ($requiredPath in $required.Keys) {
    if (-not $required[$requiredPath]) {
        Add-Violation "Required file is missing: '$requiredPath'."
    }
}

$inventoryPath = Join-Path $PublishDir "publish-inventory.json"
$inventoryJson = [ordered]@{
    schemaVersion = 1
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    targetRID = $policy.targetRID
    runtimeFrameworkVersion = $policy.runtimeFrameworkVersion
    fileCount = $inventory.Count
    files = @($inventory | Sort-Object relativePath)
} | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($inventoryPath, $inventoryJson, [System.Text.Encoding]::UTF8)

if ($script:violations -gt 0) {
    Write-Error "Publish audit failed with $script:violations violation(s)."
    exit 1
}

Write-Host "Publish audit PASSED. Explained and hashed $($inventory.Count) files in '$inventoryPath'." -ForegroundColor Green
exit 0
