# Project file format

A project is a single UTF-8 JSON file with the extension **`.srnccproj`**. It records *what you
curated* — which sources, in what priority order, what you selected, and what you pinned. It does not
contain any resource bytes, and it does not contain any index or cache data.

You never need to edit one by hand. This document exists so you can read one, diff two of them in
version control, and understand what a merge conflict is actually about.

## Shape at a glance

```json
{
  "schemaVersion": 1,
  "sources": [
    {
      "id": "3f1b0d92-6a5e-4f27-9d0a-2b7c8e5f1a44",
      "kind": "hak",
      "path": { "kind": "relative", "value": "haks/base_override.hak" },
      "fingerprint": {
        "kind": "hak",
        "algorithmVersion": 1,
        "digest": "9b1e…c4"
      }
    }
  ],
  "selectionState": {
    "defaultSelected": true,
    "overrides": [
      { "resref": "pl_skin01", "resourceType": 2022, "selected": false }
    ]
  },
  "pins": [
    {
      "resref": "c_dragred",
      "resourceType": 2002,
      "sourceId": "3f1b0d92-6a5e-4f27-9d0a-2b7c8e5f1a44",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "locator": { "kind": "hakEntry", "index": 417 }
    }
  ],
  "outputSettings": {},
  "filters": {},
  "comparisonPreferences": {}
}
```

## Fields

### Root

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | integer | `1` for files this version writes. See [Schema versions](#schema-versions) below |
| `sources` | array | Required. Priority order is **array order**: the first element is the lowest priority, the last is the highest. The source stack in the UI shows highest priority at the top, so the on-disk order is the reverse of what you see |
| `selectionState` | object | Required |
| `pins` | array | Required. May be empty |
| `outputSettings` | any | Preserved verbatim |
| `filters` | any | Preserved verbatim |
| `comparisonPreferences` | any | Preserved verbatim |

### `sources[]`

| Field | Type | Notes |
|---|---|---|
| `id` | GUID string | Stable identity of the source within this project. Pins refer to it |
| `kind` | `"hak"` or `"folder"` | Written lower-case |
| `path.kind` | `"relative"` or `"absolute"` | Written as `relative` when the source lies inside the project file's own directory, `absolute` otherwise |
| `path.value` | string | Always written with forward slashes, even on Windows. Relative values are resolved against the directory containing the project file |
| `fingerprint` | object, optional | Change detector for the source, not payload evidence |
| `fingerprint.kind` | `"hak"` or `"folder"` | |
| `fingerprint.algorithmVersion` | integer ≥ 1 | |
| `fingerprint.digest` | lower-case hex string | For a HAK, a SHA-256 over the header and table regions plus length and last-write time; for a folder, a SHA-256 over the sorted relative paths, mapping status, sizes, and last-write times |

A fingerprint tells the application whether a source changed since it was last indexed. It is
deliberately *not* a hash of the contents, and it is never used as payload evidence — payload hashes
are always recomputed at build time.

### `selectionState`

| Field | Type | Notes |
|---|---|---|
| `defaultSelected` | boolean | What a newly discovered identity does when nothing says otherwise |
| `overrides` | array | Sparse. Only identities that differ from the default appear here |
| `overrides[].resref` | string | The resref as originally written, not the canonicalised form |
| `overrides[].resourceType` | integer, 0–65535 | The numeric NWN resource type |
| `overrides[].selected` | boolean | |

Storing the default plus sparse overrides is what keeps a 188 000-asset project's file small. "Exclude
All" sets `defaultSelected` to `false` and clears the override list rather than writing 188 000
entries.

### `pins[]`

A pin overrides source-priority resolution for one identity, and carries the hash of the exact bytes
it was pinned to.

| Field | Type | Notes |
|---|---|---|
| `resref` | string | As originally written |
| `resourceType` | integer, 0–65535 | |
| `sourceId` | GUID string | Must match a `sources[].id` |
| `sha256` | 64-character lower-case hex string | SHA-256 of the pinned payload, captured when the pin was created |
| `locator.kind` | `"hakEntry"` or `"folderPath"` | |
| `locator.index` | integer ≥ 0 | Present when `kind` is `hakEntry`. The entry index within the HAK, which is how duplicate identities inside one archive stay distinguishable |
| `locator.relativePath` | string | Present when `kind` is `folderPath`. Normalised relative to the folder source root |

A pin whose source, identity, locator, or hash no longer matches is **invalid**, and the identity it
covers stays unresolved. The application never silently falls back to priority order when a pin goes
stale — that is the whole point of a pin.

## Identities are arrays, not composite keys

Selection overrides and pins use explicit `{ "resref": …, "resourceType": … }` object fields inside
arrays, rather than encoding an identity into a dictionary key such as `"pl_skin01:2022"`. That keeps
resrefs containing spaces, punctuation, or accented Windows-1252 characters readable and unambiguous.

## Schema versions

| File's `schemaVersion` | Behaviour |
|---|---|
| Less than 1 | Rejected. The project fails to open with an explicit error and the file is **not** modified |
| Exactly 1 | Opens normally, read and write |
| Greater than 1 | Opens **read-only** |

A project written by a newer version of the application opens read-only. In that state:

- the project is parsed leniently — malformed or unexpected pieces are skipped rather than treated as
  errors, so you can still look at what is there;
- **Save is disabled and Build is disabled.** Both commands report as unavailable rather than failing
  when invoked;
- Save As is still permitted, and it writes back the **original document bytes** unchanged rather
  than re-serialising the file through this version's schema. A newer project can therefore be copied
  without being downgraded.

## Corrupt project files are left alone

Unlike the cache and settings files, a project is **never** quarantined, renamed, or rebuilt. A
project file with malformed JSON, a non-object root, or a missing required section fails to open with
an error naming the file and the problem. The bytes on disk are untouched, so you can fix or restore
it yourself.

## Unknown fields are preserved

Schema 1 preserves unknown fields **recursively**. When the application saves, it starts from the
JSON document it originally read and overlays only the parts it manages:

- unrecognised fields at the root survive;
- unrecognised fields on an individual source, selection override, or pin survive, matched back to
  the right array element by source `id` or by identity, not by array position;
- `outputSettings`, `filters`, and `comparisonPreferences` are carried through whole.

So a project file written by a tool that adds its own annotations, or by a newer version that adds a
field this version does not know about, does not lose those annotations by being opened and saved
here.

## Saving is atomic

A save writes a sibling temporary file named `<project>.tmp.<32 hex characters>` in the same
directory, flushes it to disk, and then renames it over the target. An interrupted save therefore
leaves the previous project file intact plus a stray `.tmp.` file, which can be deleted.
