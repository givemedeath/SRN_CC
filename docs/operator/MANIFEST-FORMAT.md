# Manifest format

Every build produces two files: the HAK, and a provenance manifest named after it.

```
curated.hak
curated.srncc-manifest.json
```

The manifest name is always the HAK's file name with the extension replaced by
`.srncc-manifest.json`. The two are published as a transaction — you never get one without the other.
See [Recovery](RECOVERY.md) if you find them out of step.

The manifest answers one question: *what exactly is in this HAK, and where did each piece come from?*
It is indented UTF-8 JSON, written so that a human can read it and a script can check it.

## Example

```json
{
  "SchemaVersion": "1.0",
  "AppVersion": "1.0.0",
  "GeneratedUtc": "2026-08-04T13:15:02.4817355Z",
  "HakFileName": "curated.hak",
  "HakSha256Hex": "5f2c…9b",
  "TotalEntries": 2,
  "TotalSizeBytes": 148992,
  "Resources": [
    {
      "Resref": "c_dragred",
      "ResourceType": 2002,
      "ResourceTypeName": "MDL",
      "SourceLabel": "base_override.hak",
      "OriginLocator": "HakEntry(417)",
      "SizeBytes": 92160,
      "Sha256Hex": "e3b0…55",
      "IsPinned": true
    },
    {
      "Resref": "pl_skin01",
      "ResourceType": 2022,
      "ResourceTypeName": "TGA",
      "SourceLabel": "loose_textures",
      "OriginLocator": "FolderFile(textures/pl_skin01.tga)",
      "SizeBytes": 56832,
      "Sha256Hex": "a1d4…07",
      "IsPinned": false
    }
  ]
}
```

Field names are written exactly as shown, in `PascalCase`.

## Root fields

| Field | Type | Meaning |
|---|---|---|
| `SchemaVersion` | string | Version of this manifest format. `"1.0"` |
| `AppVersion` | string | Version of the application that produced the HAK |
| `GeneratedUtc` | string | When the build plan was frozen, as a round-trip UTC timestamp. **This is the only field that varies between two builds of identical content** |
| `HakFileName` | string | File name of the HAK this manifest describes, without any directory |
| `HakSha256Hex` | string | SHA-256 of the **entire HAK file**, lower-case hex. Computed by the verifier by independently reopening and rehashing the finished file |
| `TotalEntries` | integer | Number of entries in `Resources` |
| `TotalSizeBytes` | integer | Sum of every `Resources[].SizeBytes` — the payload total, which is *smaller* than the HAK file because it excludes the header and tables |
| `Resources` | array | One entry per packaged resource |

## `Resources[]` fields

| Field | Type | Meaning |
|---|---|---|
| `Resref` | string | The resref as originally written in the source, not the canonicalised lower-case form. Non-ASCII Windows-1252 characters are written literally rather than escaped |
| `ResourceType` | integer | Numeric NWN resource type, 0–65535 |
| `ResourceTypeName` | string | Upper-case canonical extension for the type, e.g. `MDL`, `TGA`, `2DA`. For a type the application does not have a registered extension for, this is the number as a string — unknown types are packaged, not dropped |
| `SourceLabel` | string | Display label of the source the resource came from: the file name of a HAK source, or the folder name of a folder source. Never a full path. If the source cannot be resolved, the source's GUID appears instead |
| `OriginLocator` | string | Where inside that source the resource came from: `HakEntry(<index>)` for a HAK entry, or `FolderFile(<relative/path>)` for a folder file. Folder paths are relative to the folder source root and always use forward slashes |
| `SizeBytes` | integer | Payload size in bytes |
| `Sha256Hex` | string | SHA-256 of the payload bytes, lower-case hex |
| `IsPinned` | boolean | Whether this resource won because you pinned it, rather than by source priority |

### On paths

The manifest never contains an absolute source path. `SourceLabel` is a display name and
`OriginLocator` is a locator inside a source — neither identifies a location on your disk. A manifest
is therefore safe to distribute alongside the HAK.

## Determinism

Two builds of the same selection over the same bytes produce a byte-identical HAK and a manifest that
differs only in `GeneratedUtc`. `HakSha256Hex` and every `Sha256Hex` are recomputed from the actual
built file — nothing in the manifest is copied forward from an earlier build or from a cached value.

## Verifying a published HAK by hand

You do not need the application to check that a HAK matches its manifest.

**1. Check the whole-file hash.** In PowerShell, from the directory holding both files:

```powershell
$m = Get-Content .\curated.srncc-manifest.json -Raw | ConvertFrom-Json
$actual = (Get-FileHash -Algorithm SHA256 (Join-Path . $m.HakFileName)).Hash.ToLowerInvariant()
if ($actual -eq $m.HakSha256Hex.ToLowerInvariant()) { "HAK matches manifest" }
else { "MISMATCH: manifest says $($m.HakSha256Hex), file is $actual" }
```

If this passes, the HAK is bit-for-bit the file the manifest was written for, and every per-resource
hash below it is implied. This single check is enough for "is this the HAK I was given?".

**2. Check the inventory.** To confirm what the HAK is supposed to contain without opening it:

```powershell
$m = Get-Content .\curated.srncc-manifest.json -Raw | ConvertFrom-Json
"$($m.TotalEntries) entries, $($m.TotalSizeBytes) payload bytes"
$m.Resources | Select-Object Resref, ResourceTypeName, SizeBytes, IsPinned, SourceLabel |
    Sort-Object Resref | Format-Table -AutoSize
```

Cross-check `TotalEntries` against `$m.Resources.Count`, and `TotalSizeBytes` against
`($m.Resources | Measure-Object SizeBytes -Sum).Sum`. A mismatch means the manifest itself was
tampered with — the application always writes them consistent.

**3. Check an individual resource.** Per-resource hashes are over the payload bytes as stored in the
HAK. Extracting a single entry to check one requires a HAK reader; in practice, step 1 is the check
worth running, because any change to any payload changes the whole-file hash.

## What a mismatch means

| Symptom | Meaning |
|---|---|
| `HakSha256Hex` does not match the file | The HAK was modified, replaced, or corrupted after publication, or the two files were paired up by hand from different builds |
| The manifest exists but the HAK does not | An interrupted publish, or someone deleted half the pair. See [Recovery](RECOVERY.md) |
| The HAK exists but the manifest does not | Same. You have a HAK you can no longer verify; rebuild to get a matching pair |
| `AppVersion` differs from the version you are running | Only informational. It records what built the HAK, and does not affect whether the HAK loads |
