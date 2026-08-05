# File locations

Everything the application writes falls into three places: its per-user data directory, your project
file, and the destination directory you choose for a build. It writes nothing to its own installation
folder.

## Per-user data — `%LOCALAPPDATA%\SRN.CC`

On a default Windows installation this expands to
`C:\Users\<you>\AppData\Local\SRN.CC`.

| Path | What it is |
|---|---|
| `cache-v1.sqlite` | SQLite index and preview cache. Holds indexed source snapshots, asset records, and compressed PNG thumbnails. Budgeted at 2 GiB of logical content, evicted least-recently-used |
| `cache-v1.sqlite-wal` | SQLite write-ahead log. The database runs in WAL mode, so this file is normal and often large |
| `cache-v1.sqlite-shm` | SQLite shared-memory index for the WAL. Normal |
| `settings.json` | Last project path, recent project paths, and the NWN:EE install override. Not created until there is something to persist |
| `Logs\` | Rolling JSON-line logs. See below |
| `Logs\srncc.log` | The active log file |
| `Logs\srncc.<timestamp>.log` | Rotated log files, timestamped `yyyyMMddTHHmmssfffZ` |
| `cache-v1.sqlite.corrupt-<timestamp>` | A quarantined cache database. See [Recovery](RECOVERY.md) |
| `settings.json.corrupt.<timestamp>` | A quarantined settings file. See [Recovery](RECOVERY.md) |

The log directory holds at most **ten** files of at most **10 MiB** each — the active `srncc.log`
plus rotated siblings. When the active file would exceed the cap it is closed, renamed with a UTC
timestamp, and a fresh `srncc.log` is started; the oldest file is deleted once there are more than
ten. That is a hard ceiling of roughly 100 MiB. The active file is opened so that other processes can
read it, so you can tail `srncc.log` while the application is running.

Note the two quarantine naming conventions differ, and this is what the code actually does:

- the cache uses a **hyphen** — `cache-v1.sqlite.corrupt-20260804T131502Z` — and quarantines the
  `-wal` and `-shm` sidecars alongside it as `cache-v1.sqlite-wal.corrupt-<timestamp>` and
  `cache-v1.sqlite-shm.corrupt-<timestamp>`;
- settings use a **dot** — `settings.json.corrupt.20260804T131502Z`.

Both timestamps are UTC in `yyyyMMddTHHmmssZ` form.

## Your project file

A project is a single JSON file you place wherever you like, with the extension **`.srnccproj`**. It
holds your sources and their priority order, selection state, pins, and preferences — not the
resources themselves. See [Project file format](PROJECT-FILE-FORMAT.md).

Saving is atomic: the application writes a sibling temporary file named
`<project>.tmp.<32 hex characters>`, flushes it to disk, and then renames it over the target. A
leftover `.tmp.` file means a save was interrupted; the original project file is untouched and the
temporary file can be deleted.

## Build output directory

A build writes into the destination directory you pick:

| Path | What it is |
|---|---|
| `<basename>.hak` | The published HAK |
| `<basename>.srncc-manifest.json` | The provenance manifest for that HAK. See [Manifest format](MANIFEST-FORMAT.md) |

While a build and publish is in flight, several short-lived files appear in the same directory:

| Path | What it is |
|---|---|
| `<32 hex characters>.tmp.hak` | The HAK being packed, before verification |
| `<32 hex characters>.tmp.manifest.json` | The manifest being generated |
| `<hakFileName>.<transactionId>.publication-journal.json` | The durable publication journal for one transaction |
| `<hakFileName>.<transactionId>.bak` | Backup of the HAK being replaced |
| `<manifestFileName>.<transactionId>.bak` | Backup of the manifest being replaced |
| `<64 hex characters>.publication.lock` | Mutual-exclusion lock over this HAK/manifest pair, held for the duration of a publish |

`<transactionId>` is 32 hexadecimal characters. All of these are deleted on a successful publish. If
any survive, a publish was interrupted — see [Recovery](RECOVERY.md) before deleting them by hand.

## What is safe to delete

| Path | Safe to delete? | Consequence |
|---|---|---|
| `cache-v1.sqlite` and its `-wal` / `-shm` sidecars | Yes, with the application closed | Every source is re-indexed and every thumbnail regenerated on next use. Slow, never lossy — the cache holds no state that is not derivable from your sources |
| `settings.json` | Yes | Recent projects, last project, and the NWN:EE install override are forgotten. Nothing about a project is lost |
| `Logs\` and everything in it | Yes | You lose diagnostic history. Nothing else |
| `*.corrupt-*` / `*.corrupt.*` quarantine files | Yes, once you have finished with them | They exist so you can inspect or report a failure. Nothing reads them |
| `%LOCALAPPDATA%\SRN.CC` entirely | Yes, with the application closed | Equivalent to a fresh install. Your projects, sources, and published HAKs are all outside it and are unaffected |
| **Your `.srnccproj` file** | **No** | This is your curation work: source order, pins, and selection overrides. It cannot be reconstructed from the cache |
| **A published `.hak` and its `.srncc-manifest.json`** | **No, not as a pair** | Deleting the manifest leaves a HAK you can no longer verify. If you no longer want the output, delete both |
| **A `.publication-journal.json` file** | **Not by hand** | It is the only record of an interrupted publish. Let the application recover it at startup first. See [Recovery](RECOVERY.md) |
| **Anything in the extracted release folder** | **No** | It is a folder deployment; removing any sibling file can stop the application from starting |

Your source HAKs and folders are never written to, so nothing in this document applies to them.
