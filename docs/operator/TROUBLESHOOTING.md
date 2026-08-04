# Troubleshooting

Start here: **the operation log at the bottom of the window** shows everything at Info level and
above as it happens, and **`%LOCALAPPDATA%\SRN.CC\Logs\srncc.log`** has the same events plus the
lower-severity detail, with structure you can query.

## Reading `srncc.log`

The log is **JSON lines**: one complete JSON object per physical line, UTF-8, newest at the end.
Messages containing newlines are escaped by the serializer, so a record can never split across two
lines — every line stands alone and `Select-String` over the file always works.

Each record carries:

| Field | Meaning |
|---|---|
| `TimestampUtc` | When the record was created, in UTC |
| `Level` | `Trace`, `Debug`, `Info`, `Warn`, or `Error` |
| `Category` | The subsystem or type that emitted it |
| `Message` | Human-readable text |
| `EventCode` | Optional stable identifier, for correlating related records |
| `Data` | Optional flat string-to-string context. Never nested, so a line stays greppable |
| `ExceptionType` | Optional full type name of an associated exception |
| `ExceptionMessage` | Optional message of that exception |

### Where it lives and how much of it there is

The active file is `Logs\srncc.log`. When a write would push it past 10 MiB it is closed, renamed to
`srncc.<yyyyMMddTHHmmssfffZ>.log`, and a fresh `srncc.log` is started. At most ten files are kept and
the oldest is deleted first, so the directory never exceeds roughly 100 MiB.

The active file is opened so other processes may read it. You can tail it live:

```powershell
Get-Content "$env:LOCALAPPDATA\SRN.CC\Logs\srncc.log" -Wait -Tail 20
```

### Useful queries

Everything at `Warn` or `Error`, newest last:

```powershell
Get-Content "$env:LOCALAPPDATA\SRN.CC\Logs\srncc.log" |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.Level -in 'Warn','Error' } |
    Select-Object TimestampUtc, Level, Category, Message
```

Everything from one subsystem across all retained files, oldest first:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\SRN.CC\Logs\srncc*.log" | Sort-Object Name |
    Get-Content | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.Category -like '*Publisher*' }
```

The startup report from the most recent launch is at the head of whichever file was active at the
time — the preflight runs before the window exists, so its records are the first thing written each
session.

### Capturing a report without the UI

`SRN.CC.App.exe --srncc-preflight-only` runs the four startup checks, writes the report to standard
output as JSON, and exits 0 without creating a window:

```powershell
& ".\SRN.CC.App.exe" --srncc-preflight-only | Out-File -Encoding utf8 preflight.json
```

That is the fastest way to get a clean, attachable snapshot of the installation's state.

## Common degraded preflight messages

A **Degraded** result is a report, not a failure. Every degradation the shipped checks can produce is
survivable, and none of them stops the application from starting. A check that throws is itself
reported as Degraded carrying the exception, so a broken check can never prevent startup either.

| What you see | What it means | What to do |
|---|---|---|
| Cache quarantined — *Quick check failed* | SQLite's integrity check rejected `cache-v1.sqlite` | Nothing. A fresh cache was created; sources re-index on next use. See [Recovery](RECOVERY.md) |
| Cache quarantined — *Unsupported cache schema version n* | The cache was written by a different version | Nothing. Same as above |
| Cache quarantined — *Cache schema version 1 is incomplete* | An interrupted first-run initialisation left a partial database | Nothing. Same as above |
| Cache quarantined — *Database initialization error: …* | The file could not be opened or read | Nothing, unless it recurs — then check disk health and antivirus |
| Cache unavailable | Quarantine itself could not complete, e.g. a read-only directory or another process holding the file | The application runs correctly but with no caching, so indexing is slower every time. Fix the permission or file lock and relaunch |
| Settings quarantined to `settings.json.corrupt.<timestamp>` | `settings.json` could not be parsed, or declared an unsupported version | Nothing. Defaults are in use; recent projects and the install override are forgotten |
| Settings are newer than this version — read-only, file untouched | `settings.json` declares a newer schema | Settings cannot be saved this session and the file is left byte-identical. Move it aside yourself if you want this version to own settings again |
| A native library is missing, named individually | One of `e_sqlite3.dll`, `av_libglesv2.dll`, `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` was not found in the application directory or in `runtimes/win-x64/native` | Re-extract the release archive completely. This is almost always a partial extraction |
| NWN:EE installation not found | No install root containing `data\nwn_base.key` was discovered via the default Steam path, the Steam registry and library folders, or the GOG registry entries | Set the install override in Settings. Base-game resources are only used to satisfy previews and dependency lookups — they are never packaged — so the application is fully usable without one |
| Explicit NWN install root is invalid | The override you set does not contain `data\nwn_base.key` | Point it at the installation root, the folder that *contains* `data`, not at `data` itself. Auto-discovered candidates are listed alongside the message |
| `render-gpu: Deferred — probed on first 3D slot` | Expected on every launch | Nothing. GPU capability is deliberately not probed at startup. See [GPU requirements](GPU-REQUIREMENTS.md) |
| A publication journal was recovered or rolled back, naming a path | An earlier publish was interrupted and has now been undone or cleaned up | Check the named directory. See [Recovery](RECOVERY.md) |
| Low free space on `%LOCALAPPDATA%\SRN.CC` | The volume holding the cache and logs is nearly full | Free space, or delete the cache — it is always safe to delete. See [File locations](FILE-LOCATIONS.md) |

## What warns versus what blocks a build

The rule is narrow and deliberate: **a build blocks only on container, identity, resolution,
source-integrity, packaging, or verification failures.** Everything else warns.

### Warns — the build still runs

| Condition | Behaviour |
|---|---|
| A preview or parser fails on a known format | Diagnostic on the row and the slot. The resource is still packaged — the application packages bytes, and it does not need to understand a file to copy it faithfully |
| A preview exceeds a safety budget — 1 MiB of hex, 8 MiB of text, an image dimension above 4096 or a decoded image above 64 MiB, or a per-family parser allocation cap | The preview degrades and reports why. Packaging is unaffected |
| Dependencies are missing or unresolved | Reported by dependency analysis so you can decide whether to add them. A gap never blocks packaging |
| A model uses unsupported features — animation, skinmesh, emitters | Reported as preview diagnostics. See [GPU requirements](GPU-REQUIREMENTS.md) |
| 3D rendering is unavailable for any reason | The slot degrades to the MDL text preview |
| A resource has an unregistered numeric type | Packaged as opaque bytes. Unknown types are supported deliberately |
| A folder file has an unsupported non-numeric extension | Stays a visible diagnostic row and is **not packageable**, so it is simply never part of a build |
| Case-only differences between occurrences | Flagged so you can see them; resolution still proceeds |

### Blocks — the build stops before anything is written

| Condition | Message you will see |
|---|---|
| The output path equals a HAK source path | `Output path '…' overlaps directly with source path '…'.` |
| The output path is inside a folder source | `Output path '…' is located inside source folder '…'.` |
| Nothing is selected | `No assets are selected for building.` |
| A selected asset is not resolved — an unresolved conflict or an invalid pin | `Selected asset '…' is not resolved (status: …). Clear invalid pins or resolve conflicts before building.` |
| A selected asset has no resolved occurrence | `Selected asset '…' has no resolved occurrence.` |
| The source behind a selected asset is unavailable | `Source for asset '…' is unavailable.` |
| The estimated output exceeds the legacy single-HAK limit | `Estimated output HAK size (…) with format overhead meets or exceeds the single-HAK limit (2,147,483,647 bytes).` The build blocks rather than splitting the output |
| Free space on the destination volume cannot be determined | `Could not locate destination volume for '…'.` or `Failed to check destination free space: …` |
| There is not enough free space | `Insufficient disk space for build artifacts. Destination volume has approximately … bytes available, but estimated requirement is … bytes.` |
| A source changed under the build — a HAK entry's identity or size no longer matches what was indexed | The pack step fails with an identity or size mismatch. Rescan and rebuild |
| Verification of the built HAK fails | `HAK build verification failed: …` listing every problem: entry-count mismatch, key-table order mismatch, per-entry size mismatch, per-entry SHA-256 mismatch, an unexpected entry, or an unexpected end of file |
| The project was opened read-only because it declares a newer schema | Build is disabled, alongside Save. See [Project file format](PROJECT-FILE-FORMAT.md) |

Verification runs against the **temporary** HAK, before publication. A build that fails verification
never touches your destination files.

## Other symptoms

| Symptom | Likely cause |
|---|---|
| The application will not start at all | Partial extraction. `SRN.CC.App.exe` needs its sibling assemblies and natives. Re-extract the whole archive |
| A source shows as unavailable | The file or folder moved or was deleted. Relocate it from the source stack, then rescan |
| A pin shows as invalid | The pinned bytes changed, or its source or locator moved. Re-pin against a readable occurrence. The application deliberately never silently falls back to priority order |
| Indexing is slow every single time | The cache is unavailable — check the cache line in the preflight report |
| A leftover `.tmp.` file next to a project | An interrupted save. The original project file is intact; delete the temporary file |
| A `.publication-journal.json` in a build directory | An interrupted publish. Do not delete it by hand — see [Recovery](RECOVERY.md) |

## What to attach when reporting a problem

1. The output of `SRN.CC.App.exe --srncc-preflight-only`.
2. `Logs\srncc.log`, plus any rotated `srncc.<timestamp>.log` covering the time of the problem.
3. The exact message text from the operation log or the failing dialog.
4. Whether the problem reproduces on a fresh `%LOCALAPPDATA%\SRN.CC` — closing the application and
   deleting that directory is always safe and tells you immediately whether the cause is persisted
   state or the inputs themselves.
