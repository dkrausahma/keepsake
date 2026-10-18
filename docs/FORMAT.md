# Keepsake Backup Repository Format – Version 1

This document describes how Keepsake stores backups, precisely enough to write an independent
reader (requirement REP-10). Everything uses standard, published algorithms.

## 1. Overview

A repository is a folder (local disk, USB disk, network share; later SFTP) with this layout:

```
keepsake.json          plain text   header: format, version, repository ID
README.txt             plain text   how to restore, for humans
config                 encrypted    chunking and pack parameters
keys/<id>              plain text   key slots: the master key, wrapped by a password or recovery key
data/<xx>/<pack-id>    encrypted    pack files with the actual file contents and folder listings
index/<id>             encrypted    which blob is stored in which pack, at which offset
snapshots/<id>         encrypted    one file per backup run
locks/<id>             encrypted    in-use markers
tools/keepsake-<ver>/  plain        portable keepsake.exe with its libraries (for restores without installation)
```

`<xx>` is the first two hex characters of the pack ID. All IDs are 64 lowercase hex characters
(256 bits). Files whose name contains `.tmp-` are incomplete uploads and must be ignored.

The repository is **self-describing**: storage location + password (or recovery key) are enough to
restore. No local database or configuration is needed.

## 2. Algorithms

| Purpose | Algorithm |
|---|---|
| Encryption | AES-256-GCM, random 96-bit nonce, 128-bit tag |
| Password → key | Argon2id (version 0x13), parameters stored per key slot |
| Key derivation | HKDF-SHA256 (RFC 5869) |
| Blob IDs | HMAC-SHA256 with a repository-specific key |
| File IDs (packs, index, snapshots) | SHA-256 of the stored (encrypted) bytes |
| Chunking | FastCDC with normalized chunking, keyed gear table |
| Compression | Zstandard (RFC 8878), level 3 |
| Serialization | UTF-8 JSON, camelCase property names |

### 2.1 Sealed data

`Seal(key, plaintext, ad)` produces `nonce (12 bytes) || ciphertext || tag (16 bytes)` using
AES-256-GCM with associated data `ad`. Where associated data is used, it is the UTF-8 string
`keepsake/v1/<kind>`, binding the ciphertext to its purpose (a snapshot cannot be swapped for an index).

### 2.2 Codec envelope

Every encrypted payload is prefixed with one codec byte before encryption:

| Byte | Meaning |
|---|---|
| `0x00` | Stored: the rest is the raw data |
| `0x01` | Zstandard: the rest is one Zstandard frame (with content size) |

Compression is only used when it saves at least ~3 %; known compressed formats (zip, jpg, mp4,
docx, …) are never compressed.

## 3. Keys

### 3.1 Master key and derived keys

The repository has one random 32-byte **master key**. It never changes. With
`salt = repository ID bytes` (hex-decoded `id` from `keepsake.json`):

```
encryptionKey = HKDF(master, salt, "keepsake/v1/encryption", 32)
idKey         = HKDF(master, salt, "keepsake/v1/blob-id",    32)
chunkerKey    = HKDF(master, salt, "keepsake/v1/chunker",    32)
```

### 3.2 Key slots (`keys/<random id>`)

Plain JSON. The master key is wrapped by a key-encryption key (KEK):

```json
{
  "version": 1,
  "type": "password",
  "label": "Initial password",
  "created": "2026-10-17T11:43:17Z",
  "host": "FILESERVER",
  "kdf": { "algorithm": "argon2id", "version": 19, "memoryKiB": 65536, "iterations": 3, "parallelism": 2, "salt": "<base64>" },
  "data": "<base64 Seal(KEK, payload, 'keepsake/v1/key/<repository id>')>"
}
```

- `password` slots: `KEK = Argon2id(UTF-8(NFC(password)), kdf)` (32 bytes).
- `recovery` slots have `"salt": "<base64 16 bytes>"` instead of `kdf`:
  `KEK = HKDF(recoveryKey, salt, "keepsake/v1/recovery-kek", 32)`.
- `payload` is the JSON `{"masterKey":"<base64>"}`.

To open a repository, try every slot of the matching type; the slot whose GCM tag verifies holds the
master key. Adding or removing passwords never re-encrypts data.

### 3.3 Recovery key text form

`KS1-` followed by Crockford Base32 (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`, most significant bit first)
of `recoveryKey (32 bytes) || first 2 bytes of SHA-256(recoveryKey)`, in groups of five separated
by `-`. When reading, case, spaces and dashes are ignored, `O` reads as `0`, `I`/`L` as `1`.

## 4. Encrypted files

`config`, `index/*`, `snapshots/*` and `locks/*` have the layout

```
0x01 || Seal(encryptionKey, codec || JSON, "keepsake/v1/<kind>")
```

with kind `config`, `index`, `snapshot` or `lock`. For `index/*` and `snapshots/*` the file name is
the SHA-256 of the complete file, so damage is detectable without the key.

### 4.1 config

```json
{ "chunkerMin": 524288, "chunkerAvg": 1048576, "chunkerMax": 8388608, "packSize": 16777216, "compression": "zstd" }
```

## 5. Blobs and packs

A **blob** is either a chunk of file content (`data`) or a folder listing (`tree`).

- Blob ID = `HMAC-SHA256(idKey, plaintext)`. Identical content always has the same ID, which is how
  deduplication works. Keying the HMAC prevents outsiders from confirming that a known file is stored.
- Stored blob = `Seal(encryptionKey, codec || plaintext, no associated data)`.

A **pack file** concatenates stored blobs and ends with a header:

```
blob_1 || blob_2 || … || blob_n || Seal(encryptionKey, header, "keepsake/v1/pack-header") || uint32 LE length of the sealed header
```

Plain header:

```
0x01 (version) || uint32 LE n || n × entry
entry = type (1 byte: 0 data, 1 tree) || blob ID (32 bytes) || uint32 LE stored length || uint32 LE plaintext length
```

Blob offsets follow from the order and lengths (first blob at offset 0). Data blobs and tree blobs
are kept in separate packs. Pack ID = SHA-256 of the complete pack file. Packs are about 16 MiB.

### 5.1 Chunking

FastCDC over the file content with min/avg/max from `config`:

- `gear[i]` = first 8 bytes (little-endian uint64) of `HMAC-SHA256(chunkerKey, uint32 LE i)` for i = 0…255.
- `bits = floor(log2(avg))`, `maskS` = top `bits+2` bits set, `maskL` = top `bits-2` bits set.
- Starting at offset `min` with `fp = 0`: `fp = (fp << 1) + gear[byte]`; cut after the byte where
  `fp & maskS == 0` (before `avg`) or `fp & maskL == 0` (between `avg` and `max`); cut at `max` at the latest.
- Files smaller than `min` are one chunk. Empty files have no chunks.

Readers do not need the chunker: a file's content is simply its blobs concatenated in order.

## 6. Index (`index/<id>`)

```json
{ "packs": [ { "id": "<pack id>", "blobs": [ { "id": "<blob id>", "type": "data", "offset": 0, "length": 1048604, "rawLength": 1048576 } ] } ] }
```

The union of all index files maps blob ID → pack, offset, stored length. A pack appears in an
index only after it has been completely written. If an index is lost, it can be rebuilt from the
pack headers.

## 7. Trees

A tree blob is the JSON of one folder listing, nodes sorted by name (ordinal):

```json
{ "nodes": [
  { "name": "report.xlsx", "type": "file", "size": 48213, "mTime": 638640000000000000, "cTime": 638640000000000000, "attributes": 32, "content": ["<blob id>", "…"] },
  { "name": "2026", "type": "dir", "size": 0, "mTime": 638640000000000000, "cTime": 638640000000000000, "attributes": 0, "subtree": "<tree blob id>" }
] }
```

- Times are UTC .NET ticks (100 ns intervals since 0001-01-01 00:00 UTC).
- `attributes`: Windows file attributes, limited to read-only (1), hidden (2), system (4),
  archive (32) and not-content-indexed (8192).
- NTFS permissions are **not** stored.

### 7.1 Path mapping

The root tree of a snapshot represents the whole machine:

| Windows path | Path in the snapshot |
|---|---|
| `D:\Data\Finance` | `D/Data/Finance` |
| `\\server\share\Projects` | `UNC/server/share/Projects` |

Folders above the selected sources (`D`, `D/Data`) are included only as far as needed to reach them.

## 8. Snapshots (`snapshots/<id>`)

```json
{
  "version": 1,
  "time": "2026-10-17T11:43:17Z",
  "host": "FILESERVER", "user": "DOMAIN\\SYSTEM", "job": "fileserver",
  "paths": ["D:\\Data"], "excludes": ["*.tmp"], "tags": [],
  "mode": "incremental",
  "tree": "<root tree blob id>",
  "parent": "<previous snapshot id>",
  "program": "Keepsake 0.1.0",
  "summary": { "filesNew": 12, "filesChanged": 3, "filesUnchanged": 49985, "filesSkipped": 0, "dirs": 6500,
               "totalBytes": 139586437120, "bytesRead": 1288490188, "bytesAdded": 356515840, "bytesStored": 125829120, "durationSeconds": 92.4 }
}
```

Every snapshot is complete on its own: `incremental` and `full` only describe how changes were
detected (`full` re-reads every file), not what is needed for a restore.

## 9. Locks (`locks/<random id>`)

```json
{ "host": "FILESERVER", "user": "…", "pid": 4242, "time": "…", "exclusive": false, "purpose": "backup" }
```

Backup, restore and check take shared locks; operations that delete data take an exclusive lock.
Locks are refreshed every 5 minutes and count as stale after 30 minutes.

## 10. Write order and crash safety

1. Pack files are written (temp file + rename).
2. Index files listing those packs are written every 20 packs / 5 minutes and at the end.
3. The snapshot is written last.

A crash or cancellation can leave packs without an index (reported by `check`, removed by a later
prune) or an index without a snapshot (reused by the next backup) – never a snapshot that points
to missing data.

## 11. Restoring without Keepsake (outline)

1. Read `keepsake.json`; derive the KEK from the password with the slot's Argon2id parameters; open
   the slot; derive `encryptionKey`.
2. Decrypt all `index/*` files to map blob IDs to packs.
3. Decrypt the wanted `snapshots/*` file, then walk the trees from `tree`.
4. For each file, read its blobs from the packs (offset/length from the index), open each with
   AES-256-GCM, strip the codec byte (decompress if `0x01`), and concatenate.
