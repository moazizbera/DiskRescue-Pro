# DiskRescue Pro

**A safety-first Windows disk-image inspection and recovery engineering project.**

DiskRescue Pro is designed to help users understand damaged or missing partition and filesystem metadata before considering recovery. Development is deliberately staged: validate image parsing first, then add discovery and file extraction with enforceable source/destination controls.

> **Development status:** this is an engineering preview, not a complete recovery utility. It supports regular disk-image files only. Do not use it on the only copy of important data. If a drive clicks, repeatedly disconnects, overheats, or returns persistent I/O errors, stop using it and consider professional recovery.

## Current release scope

The current `.NET 10` solution includes:

- A bounded, read-only random-access image reader for regular files.
- MBR parsing with signature, entry, bounds, sector-size, protective-MBR, and overlap checks.
- Independent GPT primary/backup validation, header and partition-array CRC checks, geometry checks, and validated-backup fallback.
- A WPF Windows desktop preview for image selection, partition overview, findings, JSON report export, and a selected-partition NTFS preview.
- NTFS boot-sector geometry validation, FILE record update-sequence-array fixups, bounded attribute parsing, and mapping-pairs decoding.
- MFT record reads through the initial validated unnamed `$MFT::$DATA` extent, including records crossing fragmented physical runs.
- Resident `$FILE_NAME` parsing, bounded MFT scanning, and a conservative partial directory-tree preview with parent-sequence validation, cycle detection, and orphan reporting.
- A bounded resident `$ATTRIBUTE_LIST` parser and one-level matcher against caller-supplied, already validated FILE records. It verifies record number/sequence, base-file reference, and the complete attribute identity (type, ordinal UTF-16 name, ID, and lowest VCN), with structured rejection diagnostics and explicit limits. It does **not** read referenced records, extend MFT coverage, or support non-resident lists or recursive discovery.

The attribute-list parser caps values at 16 MiB and 65,536 entries. The matcher caps candidates at 4,096 records and 16 MiB aggregate record bytes, and caps attribute parsing/comparison work at 4,096 parsed attributes and 1,000,000 comparisons. Invalid, stale, missing, duplicate, mismatched, unsupported, or ambiguous references produce diagnostics; any rejected entry makes the result incomplete.

## Safety model

- Only regular image files are opened by the current IO implementation.
- Source reads are bounded; the image reader exposes no write API.
- No physical disk enumeration, raw-device access, mounting, repair, formatting, initialization, or repartitioning is implemented.
- JSON reports are written only to a user-selected report path.
- No file-content recovery is available yet.
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

Preview NTFS metadata after obtaining a volume byte offset and length from a trusted partition report:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "C:\Images\sample.img" --offset 1048576 --length 1073741824 --max-records 5000
```

The NTFS command scans at most the configured number of initialized MFT records covered by the initial validated MFT extent. It does not yet follow additional MFT extents or directory indexes.
The attribute-list matcher is a separate in-memory library primitive; neither the CLI nor WPF preview uses it to extend MFT coverage.

## Safe test images

Generate disposable fixtures without touching physical disks:

```powershell
.\tools\New-DiskRescueTestImages.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-TestImages"
.\tools\New-NtfsMftPreviewImage.ps1 -OutputPath "$env:USERPROFILE\Documents\ntfs-mft-preview.img"
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
1. Read referenced records through a validated MFT mapping and assemble additional extents with exact VCN continuity, cycle detection, and strict resource limits.
2. Parse NTFS directory indexes and validate complete parent relationships.
3. Discover lost partition candidates from bounded filesystem-signature searches with evidence and confidence explanations.
4. Add read-only file-content mapping and integrity verification.
5. Add safe extraction to a demonstrably separate destination, with collision handling, hashes, manifest, cancellation, and resume semantics.
6. Consider physical-device imaging only after a separate read-only handle design and safety review.

See [docs/ROADMAP.md](docs/ROADMAP.md) for status and release gates.

## Contribution rules

Every feature must include automated tests, documented limitations, safe failure behavior, and source-write prevention. Never upload real user disk images, recovered personal files, credentials, or sensitive data to issues or pull requests.

## License

The project license will be finalized after dependency and distribution review.
