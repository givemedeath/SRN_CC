# SRN.CC Asset Curator

A Windows desktop application for curating Neverwinter Nights: Enhanced Edition HAK files.

SRN.CC imports HAK archives and loose-resource folders as prioritised sources, resolves the conflicts
between them, lets you compare up to three candidate resources side by side, previews resources and
their dependencies, and publishes exactly one verified HAK plus a deterministic provenance manifest.

- **Inputs are read-only.** No source HAK or folder is ever modified, renamed, or extracted.
- **Output is one HAK plus one manifest.** Multi-HAK splitting is out of scope.
- **Nothing is merged or edited.** The application selects and packages existing resources; it does
  not rewrite content, generate TLKs, modify modules, or copy base-game resources automatically.

## Supported platform

`win-x64` only. Windows on 64-bit x86 is the only supported and tested platform. The release is a
self-contained publish, so no .NET runtime installation is required.

## Obtaining and running a release

1. Download the release archive, named `SRN.CC-<version>-win-x64.zip`.
2. Extract the whole archive to a folder you can write to. Do not extract only the executable — this
   is a folder deployment, and `SRN.CC.App.exe` will not start without its sibling files.
3. Run `SRN.CC.App.exe` from the extracted folder.

On first launch the application creates its per-user data directory at `%LOCALAPPDATA%\SRN.CC` and
runs a startup preflight whose report appears in the operation log at the bottom of the window.

Full details, including what the preflight reports and how to run the window-free preflight check,
are in [Install and run](docs/operator/INSTALL-AND-RUN.md).

## Operator documentation

| Document | Read it when |
|---|---|
| [Install and run](docs/operator/INSTALL-AND-RUN.md) | Extracting the archive, first launch, and what the startup preflight reports |
| [File locations](docs/operator/FILE-LOCATIONS.md) | Finding the cache, settings, and logs — and knowing what is safe to delete |
| [Recovery](docs/operator/RECOVERY.md) | A cache or settings file was quarantined, or a publish was interrupted |
| [Project file format](docs/operator/PROJECT-FILE-FORMAT.md) | Reading or hand-inspecting a `.srnccproj` file |
| [Manifest format](docs/operator/MANIFEST-FORMAT.md) | Verifying a published HAK against its `.srncc-manifest.json` by hand |
| [GPU requirements](docs/operator/GPU-REQUIREMENTS.md) | 3D model previews are unavailable, blank, or fall back to text |
| [Troubleshooting](docs/operator/TROUBLESHOOTING.md) | Reading the log, decoding a degraded preflight, and telling a warning from a blocker |

## Licensing

This project is MIT licensed — see [LICENSE](LICENSE). Third-party components and their licences are
listed in [NOTICES.md](NOTICES.md); the release archive carries copies of every third-party licence
in its `THIRD-PARTY-LICENSES` folder.

## Repository documentation

[`PLAN.md`](PLAN.md) is the authoritative build plan and behavioural contract. Everything else under
[`docs/`](docs) other than `docs/operator/` is developer-facing: milestone plans, architecture
decision records under [`docs/adr`](docs/adr), and verification evidence.
