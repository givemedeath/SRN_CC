# Install and run

## Requirements

| | |
|---|---|
| Operating system | Windows, 64-bit x86 (`win-x64`) |
| .NET runtime | Not required — the release is self-contained |
| Disk | Space for the extracted folder, plus room under `%LOCALAPPDATA%\SRN.CC` for the cache (budgeted at 2 GiB) and logs (up to 100 MiB) |
| GPU | Only needed for 3D model previews. See [GPU requirements](GPU-REQUIREMENTS.md) — every other feature works without one |

`win-x64` is the only supported and tested platform.

## Extracting the release archive

The release ships as `SRN.CC-<version>-win-x64.zip`.

1. Extract the **entire** archive to a folder you can write to.
2. Keep the folder structure intact. This is a non-single-file publish: `SRN.CC.App.exe` is an app
   host that loads `SRN.CC.App.dll` and roughly a hundred sibling assemblies and native libraries
   from the same folder, including `e_sqlite3.dll` and the ANGLE, Skia, and HarfBuzz natives.
   Copying the `.exe` out on its own produces an application that will not start.
3. Do not extract into a folder that also holds your HAK sources or your build output. Nothing in
   the application writes to its own folder, but keeping them separate makes it obvious that it does
   not.

The archive contains only application files plus licences and notices — `LICENSE.txt`, `NOTICES.md`,
and a `THIRD-PARTY-LICENSES` folder. It contains no cache, no project files, and no logs.

## Running

Run `SRN.CC.App.exe` from the extracted folder.

## First launch

On first launch the application:

1. Creates `%LOCALAPPDATA%\SRN.CC` if it does not exist, along with the `Logs` subdirectory.
2. Creates the SQLite index and preview cache at `%LOCALAPPDATA%\SRN.CC\cache-v1.sqlite`, in WAL
   mode, so `cache-v1.sqlite-wal` and `cache-v1.sqlite-shm` sidecar files appear beside it.
3. Starts writing the rolling log at `%LOCALAPPDATA%\SRN.CC\Logs\srncc.log`.
4. Runs the startup preflight and replays its report into the operation log at the bottom of the
   window.

`settings.json` is not created until there is something to persist. Its absence on a fresh install
is normal, not a fault. See [File locations](FILE-LOCATIONS.md) for the full layout.

## What the preflight reports

The preflight runs four checks before the main window is usable. Each produces one result with a
severity of **Ok**, **Degraded**, or **Blocking**, a one-line summary, and detail lines. Every result
is written to the log and shown in the operation log.

| Check | What it looks at |
|---|---|
| Cache | Whether `cache-v1.sqlite` opened, passed its integrity check, and carries a supported schema — and whether a corrupt database was quarantined and rebuilt |
| Settings | Whether `settings.json` loaded, was quarantined as corrupt, or was written by a newer version and is therefore being treated as read-only and left untouched |
| Publication journal | Whether any interrupted publish was found and rolled back or cleaned up, naming each journal by path. It scans the working directory, any paths passed on the command line, and the directories of your recent projects |
| Tool capability | Runtime identifier and architecture, the application base directory, the presence of the required native libraries (`e_sqlite3.dll`, `av_libglesv2.dll`, `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`), the located NWN:EE installation, and free space on `%LOCALAPPDATA%\SRN.CC` |

Two things are deliberately *not* failures:

- **A Degraded result never stops the application.** Every degradation the shipped checks can produce
  is survivable — a missing native, a quarantined cache, an undiscoverable game install, and a
  read-only settings file are all reported and then worked around.
- **GPU capability is not probed at startup.** The tool-capability check reports it as deferred; the
  real probe happens the first time a 3D preview slot opens a rendering context. This is deliberate:
  creating a GL context at startup would create one for operators who never open a 3D preview. See
  [GPU requirements](GPU-REQUIREMENTS.md).

A check that throws is itself reported as Degraded, carrying the exception — a failing check can
never prevent startup.

If any preflight line is unfamiliar, [Troubleshooting](TROUBLESHOOTING.md) decodes the common ones.

### Running the preflight without a window

`SRN.CC.App.exe --srncc-preflight-only` runs the same four checks, writes the startup report to
standard output as JSON, and exits with code 0 without creating a window. Use it to confirm an
installation on a machine you cannot log into interactively, or to capture the report as a file.

## Inputs are read-only

The application never modifies its inputs. This is a contract, not a convention:

- Source HAKs are opened `FileAccess.Read` with `FileShare.Read`, so other readers are allowed but
  writes and deletes are refused for as long as the handle is open. Every resource is read through a
  bounded stream that cannot address bytes outside its validated range.
- An archive is never extracted to index or preview it. There is no staging directory of unpacked
  resources anywhere.
- Folder sources are read, never written, never renamed, and never reorganised. A relative path that
  would escape the source root is rejected.
- A build refuses an output path that equals any HAK source, or that lies inside any folder source.
- Every resource opened during a build is rechecked against what was indexed: a HAK entry whose
  identity or size no longer matches is refused rather than packaged, and the verifier independently
  reopens the finished HAK and compares every entry's size and SHA-256 before anything is published.

The only paths the application writes to are its own directory under `%LOCALAPPDATA%\SRN.CC`, your
project file, and the destination directory you choose for a build.
