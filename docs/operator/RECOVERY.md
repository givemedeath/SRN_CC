# Recovery

Three things can go wrong between one launch and the next: the cache can be corrupted, the settings
file can be corrupted or written by a newer version, and a publish can be interrupted part-way. All
three are detected and handled by the startup preflight, and none of them can lose your curation
work or a previously published HAK.

## A quarantined cache

**What you will see.** A preflight line reporting that the cache was quarantined, with a reason, and
a file in `%LOCALAPPDATA%\SRN.CC` named:

```
cache-v1.sqlite.corrupt-<yyyyMMddTHHmmssZ>
cache-v1.sqlite-wal.corrupt-<yyyyMMddTHHmmssZ>
cache-v1.sqlite-shm.corrupt-<yyyyMMddTHHmmssZ>
```

The `-wal` and `-shm` sidecars are quarantined alongside the database only if they exist. The
diagnostic code recorded in the log is `CorruptedCacheQuarantined`.

**What it means.** On startup the application opens `cache-v1.sqlite` and runs SQLite's integrity
check, then confirms the schema version is supported and that its tables carry every required
column. Any of the following triggers quarantine:

| Reason reported | Cause |
|---|---|
| Quick check failed | SQLite's own integrity check rejected the file — usually truncation or a bad sector |
| Unsupported cache schema version *n* | The file was written by a different version of the application |
| Cache schema version 1 is incomplete | The expected tables or columns are missing — an interrupted first-run initialisation |
| Database initialization error: *message* | The file could not be opened or read at all |

**What to do.** Nothing. The corrupt file is renamed aside and a fresh, empty database is created in
its place. Your sources are re-indexed on next use, which is slower than a warm cache but produces
identical results — the cache holds nothing that is not derivable from your sources. Delete the
quarantined files whenever you are done with them.

**If the quarantine itself fails** — for example the directory is read-only, or another process holds
the database open — the application does not crash. The cache is marked unavailable, the preflight
reports it as Degraded, and every cache operation behaves exactly as it does for a cold cache: a miss
that is recomputed. The application is fully usable in this state, just slower. Fix the underlying
permission or file-lock problem and relaunch to get the cache back.

## A quarantined or read-only settings file

There are two distinct outcomes, and telling them apart matters.

**Corrupt settings are quarantined.** If `settings.json` cannot be parsed, or declares a schema
version the application does not support, it is renamed to
`settings.json.corrupt.<yyyyMMddTHHmmssZ>` — note the **dot** before the timestamp, unlike the
cache's hyphen — and defaults are used. The preflight reports the quarantined path. You lose recent
projects, the last project path, and the NWN:EE install override. Nothing else.

**Newer settings are left alone.** If `settings.json` declares a schema version *newer* than this
application understands, it is **not** quarantined and **not** modified. The application starts with
defaults, marks settings read-only, says so in the preflight, and refuses to save settings for the
rest of the session. The file on disk stays byte-identical, so a newer version of the application
will find its settings exactly as it left them.

If you deliberately want this application to own settings again, close it, move the newer
`settings.json` somewhere else yourself, and relaunch.

## An interrupted publish

Publishing a build is a transaction over two files — the HAK and its manifest — recorded in a durable
journal so that a crash, a power loss, or a kill mid-write can always be undone.

### What a `.publication-journal.json` file is

A journal is a small JSON file written into the **destination directory**, beside the HAK being
published, named:

```
<hakFileName>.<transactionId>.publication-journal.json
```

`<hakFileName>` is the full destination file name including its extension, and `<transactionId>` is
32 hexadecimal characters unique to that publish. So publishing `curated.hak` produces a journal
called something like `curated.hak.4f3c…d1.publication-journal.json`.

It records the destination paths, the temporary file paths, the backup paths, whether a HAK and a
manifest existed before the publish started, and the state the transaction reached. It is rewritten —
atomically, flushed to disk, then renamed over itself — before each step, so the file on disk always
describes work that has been *intended*, never work that has silently happened.

The journal is deleted on a successful publish. **A journal file that is still present means the
publish did not finish.**

### The five states

| State | Reached when | Undo on recovery |
|---|---|---|
| `Prepared` | The journal has been written; nothing on disk has changed yet | Delete temporary files and the journal |
| `BackedUp` | Any existing HAK and manifest have been copied to `.bak` files | Delete temporary files, backups, and the journal |
| `HakReplaced` | The intent to replace the HAK has been recorded, and the HAK move follows | Restore the HAK from its backup, or delete it if there was none before |
| `ManifestReplaced` | The intent to replace the manifest has been recorded, and the manifest move follows | Restore both the HAK and the manifest from backups, or delete either that did not exist before |
| `Committed` | Both files are in place and the transaction is durably marked complete | Nothing to undo — recovery only deletes the leftover backups and the journal |

The manifest is replaced **last**, and `Committed` is written before any cleanup. That ordering is
what makes the transaction recoverable: a journal found in any state below `Committed` means the
destination pair must be rolled back, and a journal found in `Committed` means the pair is good and
only cleanup was interrupted.

### How recovery happens

Recovery runs as one of the four startup preflight checks. It scans for journals and, for each one it
finds, takes the publication lock for that HAK/manifest pair and then either rolls the transaction
back or finishes the cleanup, according to the state above. Each recovered or rolled-back journal is
reported by path in the preflight report and in the log.

The directories it scans are:

- the working directory the application was started from;
- any directory passed on the command line at startup;
- the directories of your recent projects, from `settings.json`.

If your build destination is none of those, open the project first — its directory becomes a recent
project — and relaunch, or start the application from the destination directory.

Rollback also deletes the transaction's temporary files, its `.bak` backups, and the journal itself.

### Doing it by hand

Prefer letting the application recover. If you must inspect a destination directory yourself:

- `<hakFileName>.<transactionId>.bak` and `<manifestFileName>.<transactionId>.bak` are the previous
  contents of the two published files. If a rollback never ran, these are your originals.
- `<32 hex characters>.tmp.hak` and `<32 hex characters>.tmp.manifest.json` are unfinished build
  artefacts and are always safe to delete.
- `<64 hex characters>.publication.lock` is a lock file. A process waits up to 20 seconds for it
  before giving up with a timeout. If no instance of the application is running and the lock file
  persists, it is stale and can be deleted.
- **Do not delete the journal before recovery has run.** It is the only thing that knows which of the
  two files was replaced and which backup belongs to which.

A previously published, valid HAK and manifest pair is never left in a mixed state: either the new
pair is committed, or the old pair is restored.
