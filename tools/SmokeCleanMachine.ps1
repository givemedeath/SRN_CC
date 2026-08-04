# tools/SmokeCleanMachine.ps1
# Clean-machine acceptance smoke over an extracted release archive.
#
# This is the only check that exercises the shipped artifact the way a user receives it: an expanded
# ZIP on a machine that has never run the application. It runs in two halves.
#
#   1. A deterministic half. `--srncc-preflight-only` writes the StartupReport as JSON to stdout,
#      exits 0, and creates no window, so the report can be parsed and asserted regardless of
#      whether the host can realize an Avalonia window.
#   2. A windowed half. The application is launched normally and must survive ten seconds, having
#      created its log and its cache under %LOCALAPPDATA%\SRN.CC.
#
# The two halves are separate on purpose: a hosted runner that cannot realize a window can have the
# second half relaxed without losing the first, which is the real gate.
[CmdletBinding()]
param(
    # The extracted release tree containing SRN.CC.App.exe.
    [Parameter(Mandatory = $true)]
    [string]$AppRoot,

    # Seconds the windowed process must survive.
    [int]$WindowedSurvivalSeconds = 10,

    # Seconds --srncc-preflight-only is allowed before it is treated as having opened a window or
    # blocked on input. A windowed run never returns on its own; the preflight run always does.
    [int]$PreflightTimeoutSeconds = 120,

    # Local re-runs happen on a developer machine that already has %LOCALAPPDATA%\SRN.CC. The switch
    # downgrades the clean-machine precondition to a warning and switches the log/cache assertions
    # from "were created" to "were written during this run". CI must never pass it.
    [switch]$AllowExistingAppData
)

$ErrorActionPreference = "Stop"

# Every check the application is expected to run at startup. Asserting the set, not just the count,
# is what makes "every check ran" meaningful: a check silently dropped from the wiring would
# otherwise pass a count-only assertion the moment another check is added.
$expectedCheckIds = @("settings", "cache", "publication-journal", "tool-capability")

$appDataRoot = Join-Path $env:LOCALAPPDATA "SRN.CC"
$logPath = Join-Path $appDataRoot "Logs/srncc.log"
$cachePath = Join-Path $appDataRoot "cache-v1.sqlite"

$AppRoot = [System.IO.Path]::GetFullPath($AppRoot)
$exePath = Join-Path $AppRoot "SRN.CC.App.exe"

function Fail([string]$Message) {
    throw "CLEAN-MACHINE SMOKE FAILURE: $Message"
}

function Read-SharedText([string]$Path) {
    # The application may still hold the file; JsonLineLogSink opens append per record, but a read
    # that raced one would fail with an exclusive share mode.
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $stream.Dispose()
    }
}

# Severity may serialize as its name ("Blocking") or as its ordinal (2) depending on the converter
# the application configures. Both are accepted and normalized; anything else is a contract break
# rather than a pass.
function ConvertTo-SeverityName([object]$Value) {
    $names = @("Ok", "Degraded", "Blocking")
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) {
        $match = @($names | Where-Object { $_ -eq $Value })
        if ($match.Count -eq 1) { return $match[0] }
        return $null
    }
    $index = -1
    if ([int]::TryParse([string]$Value, [ref]$index) -and $index -ge 0 -and $index -lt $names.Count) {
        return $names[$index]
    }
    return $null
}

function Get-CaseInsensitiveProperty([object]$Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = @($Object.PSObject.Properties | Where-Object { $_.Name -eq $Name })
    if ($property.Count -eq 0) { return $null }
    return $property[0].Value
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " SRN.CC clean-machine smoke" -ForegroundColor Cyan
Write-Host " Release tree: $AppRoot" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

if (-not (Test-Path $AppRoot -PathType Container)) { Fail "The release tree '$AppRoot' does not exist." }
if (-not (Test-Path $exePath -PathType Leaf)) { Fail "'$exePath' is missing from the release tree." }

# --- 1. The machine is clean ----------------------------------------------------------------------
$appDataExisted = Test-Path $appDataRoot
if ($appDataExisted) {
    if (-not $AllowExistingAppData) {
        Fail "'$appDataRoot' already exists; this is not a clean machine, so first-run behaviour cannot be observed."
    }
    Write-Warning "'$appDataRoot' already exists. -AllowExistingAppData was passed, so the log and cache are asserted to have been written during this run rather than created by it."
} else {
    Write-Host "[1/5] '$appDataRoot' is absent; the machine is clean." -ForegroundColor Green
}
$runStartUtc = [DateTime]::UtcNow.AddSeconds(-2)

# --- 2. The release tree passes the release audit -------------------------------------------------
Write-Host "[2/5] Auditing the extracted release tree..." -ForegroundColor Cyan
& "$PSScriptRoot/AuditRelease.ps1" -Root $AppRoot
if ($LASTEXITCODE -ne 0) { Fail "AuditRelease.ps1 failed against '$AppRoot' with exit code $LASTEXITCODE." }

# --- 3. The deterministic preflight ---------------------------------------------------------------
Write-Host "[3/5] Running --srncc-preflight-only and parsing the startup report..." -ForegroundColor Cyan
$outPath = Join-Path ([System.IO.Path]::GetTempPath()) ("srncc-preflight-out-" + [Guid]::NewGuid().ToString("N") + ".json")
$errPath = [System.IO.Path]::ChangeExtension($outPath, ".err.txt")
try {
    $preflight = Start-Process -FilePath $exePath -ArgumentList "--srncc-preflight-only" `
        -NoNewWindow -PassThru -RedirectStandardOutput $outPath -RedirectStandardError $errPath

    # Dereferencing .Handle caches the Win32 handle inside the Process object. Without this the
    # handle is released the moment the process exits and .ExitCode reads back as $null, which the
    # comparison below would then report as a failure with a blank exit code.
    $null = $preflight.Handle

    # A windowed launch never returns on its own, so returning unaided within the timeout is the
    # observable form of "creates no window" available to a script.
    if (-not $preflight.WaitForExit($PreflightTimeoutSeconds * 1000)) {
        Stop-Process -Id $preflight.Id -Force
        Fail "--srncc-preflight-only did not exit within $PreflightTimeoutSeconds second(s); it must run window-free and terminate."
    }
    if ($preflight.ExitCode -ne 0) {
        $stderrText = ""
        if (Test-Path $errPath) { $stderrText = (Read-SharedText $errPath).Trim() }
        Fail "--srncc-preflight-only exited with code $($preflight.ExitCode). stderr: $stderrText"
    }

    $stdout = ""
    if (Test-Path $outPath) { $stdout = (Read-SharedText $outPath).Trim() }
    if ([string]::IsNullOrWhiteSpace($stdout)) { Fail "--srncc-preflight-only wrote nothing to stdout; the startup report is the contract." }

    try {
        $report = $stdout | ConvertFrom-Json
    } catch {
        Fail "--srncc-preflight-only stdout is not valid JSON. Human-readable output belongs on stderr. First 400 characters: $($stdout.Substring(0, [Math]::Min(400, $stdout.Length)))"
    }
} finally {
    foreach ($scratch in @($outPath, $errPath)) {
        if (Test-Path $scratch) { Remove-Item $scratch -Force }
    }
}

$results = Get-CaseInsensitiveProperty $report "results"
if ($null -eq $results) { Fail "The startup report JSON has no 'results' array." }
$results = @($results)
if ($results.Count -eq 0) { Fail "The startup report ran no checks." }

$observed = @{}
foreach ($result in $results) {
    $checkId = Get-CaseInsensitiveProperty $result "checkId"
    if ([string]::IsNullOrWhiteSpace($checkId)) { Fail "A startup check result carries no checkId." }
    if ($checkId -eq "unknown-check") { Fail "A startup check could not even be identified; StartupPreflight substituted 'unknown-check'." }

    $severityRaw = Get-CaseInsensitiveProperty $result "severity"
    $severity = ConvertTo-SeverityName $severityRaw
    if ($null -eq $severity) { Fail "Startup check '$checkId' reported an unrecognized severity '$severityRaw'." }
    if ($severity -eq "Blocking") {
        Fail "Startup check '$checkId' is Blocking on a clean machine: $(Get-CaseInsensitiveProperty $result 'summary')"
    }

    if ($observed.ContainsKey($checkId)) { Fail "Startup check '$checkId' reported more than once." }
    $observed[$checkId] = $severity
    Write-Host "  $checkId : $severity"
}

$missing = @($expectedCheckIds | Where-Object { -not $observed.ContainsKey($_) })
if ($missing.Count -gt 0) { Fail "Startup checks did not run: $($missing -join ', ')." }
$unexpected = @($observed.Keys | Where-Object { $expectedCheckIds -notcontains $_ })
if ($unexpected.Count -gt 0) { Fail "The startup report contains unrecognized checks: $($unexpected -join ', '). Update `$expectedCheckIds in this script when a check is added." }

# --- 4. Windowed survival --------------------------------------------------------------------------
Write-Host "[4/5] Launching the application and holding it for $WindowedSurvivalSeconds second(s)..." -ForegroundColor Cyan
$app = $null
try {
    $app = Start-Process -FilePath $exePath -PassThru
    $null = $app.Handle   # See the preflight launch: .HasExited and .ExitCode need the cached handle.
    Start-Sleep -Seconds $WindowedSurvivalSeconds
    if ($app.HasExited) {
        Fail "The application exited after less than $WindowedSurvivalSeconds second(s) with code $($app.ExitCode)."
    }

    # --- 5. First-run state ------------------------------------------------------------------------
    Write-Host "[5/5] Verifying the first-run log and cache under '$appDataRoot'..." -ForegroundColor Cyan
    foreach ($required in @($logPath, $cachePath)) {
        if (-not (Test-Path $required -PathType Leaf)) {
            Fail "First run did not create '$required'."
        }
    }

    # On a genuinely clean machine the files above did not exist a moment ago, so their presence is
    # already proof this run created them. Under -AllowExistingAppData only the log can be checked
    # for freshness: it is appended per record, so its mtime always advances. The cache cannot be —
    # opening an existing healthy database journals into the -wal sidecar and never touches the
    # main file, so asserting its mtime would fail on every second run for no defect at all.
    if ($appDataExisted -and (Get-Item $logPath).LastWriteTimeUtc -lt $runStartUtc) {
        Fail "'$logPath' was not written during this run; it predates the smoke."
    }

    $logText = Read-SharedText $logPath
    $firstLine = @($logText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    if ($firstLine.Count -eq 0) { Fail "'$logPath' is empty; the log must be written from the first record." }
    try {
        $firstLine[0] | ConvertFrom-Json | Out-Null
    } catch {
        Fail "The first line of '$logPath' is not JSON: $($firstLine[0])"
    }
    Write-Host "  Log and cache present; the first log line parses as JSON." -ForegroundColor Green
} finally {
    if ($app -and -not $app.HasExited) {
        Stop-Process -Id $app.Id -Force
        $app.WaitForExit()
    }
}

Write-Host "Clean-machine smoke PASSED against '$AppRoot'." -ForegroundColor Green
exit 0
