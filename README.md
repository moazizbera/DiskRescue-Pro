# DiskRescue Pro

**A safety-first Windows disk-image inspection and recovery engineering project.**

DiskRescue Pro is designed to help users understand damaged or missing partition and filesystem metadata before considering recovery. Development is deliberately staged: validate image parsing first, then add discovery and file extraction with enforceable source/destination controls.

> **Development status:** this is an engineering preview, not a complete recovery utility. Partition inspection and lost-partition scanning currently operate on regular disk-image files only; physical-device support is experimental inventory/preflight code, not a recovery workflow. Do not use it on the only copy of important data. If a drive clicks, repeatedly disconnects, overheats, or returns persistent I/O errors, stop using it and consider professional recovery.

## Current release scope

The current `.NET 10` solution includes:

- A bounded, read-only random-access image reader for regular files.
- MBR parsing with signature, entry, bounds, sector-size, protective-MBR, and overlap checks.
- Independent GPT primary/backup validation, header and partition-array CRC checks, geometry checks, and validated-backup fallback.
- A WPF Windows desktop preview for image selection, partition overview, findings, JSON report export, selected-partition NTFS preview, and cancellable progress-reporting lost-NTFS discovery over image files.
- A bounded, chunked NTFS boot-sector scanner that validates primary BPB geometry, corroborates the backup boot sector, and classifies candidates as existing, overlapping, or lost relative to validated MBR/GPT partition ranges. Lost candidates can be selected for the current bounded NTFS metadata preview.
- NTFS boot-sector geometry validation, FILE record update-sequence-array fixups, bounded attribute parsing, and mapping-pairs decoding.
- MFT record reads through the initial validated unnamed `$MFT::$DATA` extent, including records crossing fragmented physical runs.
- Resident `$FILE_NAME` parsing, bounded MFT scanning, and a conservative partial directory-tree preview with parent-sequence validation, cycle detection, and orphan reporting.
- A bounded resident `$ATTRIBUTE_LIST` parser and matcher that verifies record number/sequence, base-file reference, and complete attribute identity (type, ordinal UTF-16 name, ID, and lowest VCN), with structured rejection diagnostics and explicit limits.
- A bounded first-stage MFT continuation workflow: it parses record 0's resident `$ATTRIBUTE_LIST`, reads only referenced FILE records already covered by the current validated MFT map, authenticates record/sequence/base-reference and attribute identity, then assembles non-sparse unnamed `$MFT::$DATA` extents with exact VCN continuity and physical-run overlap checks. It can extend reads beyond the initial extent when the continuation FILE records are themselves in that initial map. It fails closed for non-resident attribute lists or references outside the currently validated map; recursive/bootstrap discovery is not implemented.
- A bounded file-data stream reader for resident and single-extent non-sparse, uncompressed, unencrypted NTFS `$DATA` attributes. It enforces a caller-supplied export byte ceiling, bounded 1 MiB copying, cancellation, volume/image bounds, and zero-fills bytes beyond initialized size. A CLI extraction command writes through a temporary file and publishes only to a new destination. Multi-extent file streams, sparse/compressed/encrypted streams, directory-index traversal, and full recovery-tree traversal remain unsupported.
- An experimental Windows-only physical-disk inventory foundation: bounded `PhysicalDriveN` metadata probes, best-effort vendor/model/serial/bus identity, volume-GUID-to-disk extent mapping, serial/geometry revalidation on reader open, and a conservative per-device scan preflight with explicit blockers. This foundation is not integrated into partition scanning or acquisition.

The attribute-list parser caps values at 16 MiB and 65,536 entries. The matcher caps candidates at 4,096 records and 16 MiB aggregate record bytes, and caps attribute parsing/comparison work at 4,096 parsed attributes and 1,000,000 comparisons. Invalid, stale, missing, duplicate, mismatched, unsupported, or ambiguous references produce diagnostics; any rejected entry makes the result incomplete.

The in-memory MFT extent-map assembler caps input at 4,096 extents, 1,000,000 decoded runs, and 16 MiB aggregate mapping-pairs bytes. These are parser resource limits, not a claim that continuation discovery is complete.

## Safety model

- Partition inspection and lost-partition scanning currently open regular image files only. The separate experimental physical reader is not connected to those workflows.
- Source reads are bounded; the image reader exposes no write API.
- Physical-disk enumeration is metadata-only. The experimental raw reader requests `GENERIC_READ` and exposes bounded reads, but is not connected to the partition scanner or recovery UI. Windows or another process may still write through mounted filesystems; a read-only handle does not make the underlying disk immutable.
- Physical sector scanning remains disabled: the app cannot yet independently verify firmware boot-device exclusion or complete imaging-first safeguards. The preflight therefore remains blocked. No mounting, repair, formatting, initialization, or repartitioning is implemented.
- JSON reports are written only to a user-selected report path.
- File-data extraction is image-file-only and deliberately limited to validated resident or single-extent, non-sparse, uncompressed, unencrypted streams. It is not yet a complete recovery workflow: filenames are not automatically exported, multi-extent streams and directory indexes are unsupported, and destination separation from the original physical device cannot be established by this image-only command.
- All automated integration checks use generated disposable images.
- If file extraction is introduced, output must be written to a verified destination separate from the source.

A warning in the UI is not sufficient protection: future device access must enforce read-only permissions in the device-access layer itself. Do not run filesystem repair commands against a suspect source.

## Build and test

Install the .NET 10 SDK:

```powershell
dotnet restore DiskRescue.sln
dotnet build DiskRescue.sln --configuration Release --no-restore
dotnet test DiskRescue.sln --configuration Release --no-build
```

Inspect an image:

```powershell
dotnet run --project src/DiskRescue.Cli -- inspect "C:\Images\sample.img"
```

Inspect a 4Kn image:

```powershell
dotnet run --project src/DiskRescue.Cli -- inspect "C:\Images\sample-4kn.img" --sector-size 4096
```

Copy a regular image file to a **new** destination and calculate SHA-256 during the copy:

```powershell
dotnet run --project src/DiskRescue.Cli -- image-copy "C:\Images\source.img" "D:\DiskRescue\source-copy.img"
```

The destination directory must already exist and the destination file must not exist. The command reports progress and the resulting SHA-256; Ctrl+C cancels acquisition and removes the incomplete temporary output when possible. This command accepts regular image files only. It does not open physical disks or verify that the destination is on separate physical storage. Do not use it as a substitute for the still-unimplemented physical-drive acquisition workflow.

Preview NTFS metadata after obtaining a volume byte offset and length from a trusted partition report:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "C:\Images\sample.img" --offset 1048576 --length 1073741824 --max-records 5000
```

The NTFS command attempts bounded resident `$ATTRIBUTE_LIST` continuation discovery before scanning. It can include additional MFT extents only when their continuation FILE records are already reachable through the initial validated MFT map. Non-resident lists, recursive bootstrap discovery, and directory indexes are not followed.

Extract one validated NTFS file data stream by MFT record number:

```powershell
dotnet run --project src/DiskRescue.Cli -- extract-ntfs-file "C:\Images\sample.img" --offset 1048576 --length 1073741824 --record 42 --output "D:\Recovered\record-42.bin" --max-bytes 268435456
```

The output directory must already exist and the output file must not exist. The command requires an explicit maximum export size, validates the boot sector, MFT and stream mapping, follows resident streams and non-resident streams whose extents are fully matched through a resident `$ATTRIBUTE_LIST` (with every continuation FILE record already reachable in the validated MFT map), writes to a temporary file, flushes it, then publishes without overwrite. Sparse, compressed, encrypted, non-resident attribute lists, and continuation records outside the current MFT map remain unsupported. Ctrl+C cancels and removes the temporary output when possible. This is a low-level image-only stream export, not automatic filename-based recovery. Do not use it to write recovered files onto the suspect source disk.

## Safe test images

Generate disposable fixtures without touching physical disks:

```powershell
.\tools\New-DiskRescueTestImages.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-TestImages"
.\tools\New-NtfsMftPreviewImage.ps1 -OutputPath "$env:USERPROFILE\Documents
tfs-mft-preview.img"
```

See [docs/TEST-IMAGES.md](docs/TEST-IMAGES.md) for expected results and desktop testing instructions.

## Windows preview artifacts

Successful Windows CI runs publish:
- `DiskRescue-Pro-Desktop-win-x64-preview`
- `DiskRescue-Pro-win-x64-preview`
- `diskrescue-test-results`

Download them from the [GitHub Actions workflow](https://github.com/moazizbera/DiskRescue-Pro/actions/workflows/ci.yml).

## Repository map

- `src/DiskRescue.IO` — bounded read-only image access.
- `src/DiskRescue.Partitions` — MBR/GPT parsing.
- `src/DiskRescue.Filesystems` — NTFS metadata parsers and bounded MFT preview.
- `src/DiskRescue.Cli` — inspection commands and JSON output.
- `src/DiskRescue.Desktop` — WPF user interface.
- `tests` — parser, bounds, malformed metadata, and generated-image tests.
- `tools` — safe synthetic image generators.
- `docs` — architecture, safety policy, roadmap, and test procedures.

## Roadmap

The next critical milestones are:
1. Complete recursive and non-resident MFT continuation discovery with exact VCN continuity, cycle detection, and strict resource limits.
2. Parse NTFS directory indexes and validate complete parent relationships; export files using verified names and safe path handling.
3. Add multi-extent file streams, sparse-file handling, compressed/encrypted stream diagnostics, and stronger recovered-content integrity verification.
4. Extend image-based lost-partition discovery to FAT32 and exFAT, improve candidate deduplication/conflict analysis, and expand generated regression fixtures. The current scanner is NTFS-only and does not access physical disks.
5. Add recovery manifests, destination-volume verification, resume semantics, and end-to-end extraction validation.
6. Complete firmware/system boot-device exclusion, device identity verification, mounted-volume safety, cancellation and I/O failure handling; then implement imaging to separate storage before enabling any physical partition scan. The current physical reader is only a low-level foundation and must not be used against the original source drive.

See [docs/ROADMAP.md](docs/ROADMAP.md) for status and release gates.

## Contribution rules

Every feature must include automated tests, documented limitations, safe failure behavior, and source-write prevention. Never upload real user disk images, recovered personal files, credentials, or sensitive data to issues or pull requests.

## License

The project license will be finalized after dependency and distribution review.
