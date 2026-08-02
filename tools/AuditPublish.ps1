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

$policyPath = Join-Path $repoRoot "eng/publish-policy.json"
if (-not (Test-Path $policyPath)) {
    Write-Error "Publish policy file not found: $policyPath"
    exit 1
}

if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish directory does not exist: $PublishDir"
    exit 1
}

$policy = Get-Content $policyPath -Raw | ConvertFrom-Json
Write-Host "Auditing publish directory: $PublishDir..." -ForegroundColor Cyan

$violations = 0
$inventory = @()

$publishedFiles = Get-ChildItem -Path $PublishDir -Recurse -File

foreach ($file in $publishedFiles) {
    $relPath = $file.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $ext = $file.Extension.ToLower()
    $name = $file.Name

    # Check forbidden extensions
    if ($policy.forbiddenExtensions -contains $ext) {
        Write-Host "PUBLISH AUDIT ERROR: Forbidden extension '$ext' found in publish file: $relPath" -ForegroundColor Red
        $violations++
    }

    # Check forbidden assemblies
    if ($policy.forbiddenAssemblies -contains $name) {
        Write-Host "PUBLISH AUDIT ERROR: Forbidden assembly '$name' found in publish directory." -ForegroundColor Red
        $violations++
    }

    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
    $inventory += @{
        relativePath = $relPath
        sizeBytes = $file.Length
        sha256 = $hash
    }
}

# Verify required files
foreach ($req in $policy.requiredFiles) {
    $found = $inventory | Where-Object { $_.relativePath -eq $req }
    if (-not $found) {
        Write-Host "PUBLISH AUDIT ERROR: Required file missing from publish output: $req" -ForegroundColor Red
        $violations++
    }
}

# Output inventory file
$inventoryPath = Join-Path $PublishDir "publish-inventory.json"
$inventoryJson = @{
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    fileCount = $inventory.Count
    files = $inventory
} | ConvertTo-Json -Depth 4

[System.IO.File]::WriteAllText($inventoryPath, $inventoryJson, [System.Text.Encoding]::UTF8)

if ($violations -gt 0) {
    Write-Error "Publish audit failed with $violations violation(s)."
    exit 1
}

Write-Host "Publish audit PASSED. Generated inventory at $inventoryPath." -ForegroundColor Green
exit 0
