# DiskRescue Pro

**A safety-first Windows disk-image inspection and recovery engineering project.**

DiskRescue Pro is designed to help users understand damaged or missing partition and filesystem metadata before considering recovery. Development is deliberately staged: validate image parsing first, then add discovery and file extraction with enforceable source/destination controls.

> **Development status:** this is an engineering preview, not a complete recovery utility. Partition inspection and lost-partition scanning currently operate on regular disk-image files only; physical-device support is experimental read-only imaging/preflight code; physical partition scanning and recovery are not enabled. Do not use it on the only copy of important data. If a drive clicks, repeatedly disconnects, overheats, or returns persistent I/O errors, stop using it and consider professional recovery.

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
- A bounded file-data stream reader for supported NTFS `$DATA` attributes. Opt-in deleted-file extraction uses a separate preflight-gated path that verifies deleted base/continuation FILE-record identity, parent identity, full VCN/LCN mapping, and current `$Bitmap` allocation across resident or validated multi-extent streams before reading. Sparse, compressed, encrypted, non-resident `$ATTRIBUTE_LIST` bootstrap, and continuation records outside the initially validated MFT map remain unsupported. Exports enforce a caller-supplied byte ceiling, bounded copying, cancellation, volume/image bounds, and zero-fill beyond initialized size. CLI extraction writes through a temporary file and publishes only to a new destination. A successful deleted-data read does not prove original-content integrity or overwrite history.
- A fail-closed Windows boot-critical disk mapper that identifies the running Windows installation volume, GPT EFI System Partition volumes, and active/EFI MBR partitions; any incomplete partition inspection blocks preflight. This does not attest firmware boot order.
- A Windows-only physical acquisition foundation: bounded `PhysicalDriveN` metadata probes, best-effort vendor/model/serial/bus identity, volume-GUID-to-disk extent mapping, boot-critical disk exclusion, source identity/geometry revalidation, and a guarded acquisition coordinator that verifies destination-volume separation twice before opening the source read-only. It copies to a new image, verifies exact length and SHA-256 before publishing, and writes a JSON acquisition manifest. The coordinator is exposed through an experimental WPF acquisition window and CLI flow, but has not been validated on physical hardware.

The attribute-list parser caps values at 16 MiB and 65,536 entries. The matcher caps candidates at 4,096 records and 16 MiB aggregate record bytes, and caps attribute parsing/comparison work at 4,096 parsed attributes and 1,000,000 comparisons. Invalid, stale, missing, duplicate, mismatched, unsupported, or ambiguous references produce diagnostics; any rejected entry makes the result incomplete.

The in-memory MFT extent-map assembler caps input at 4,096 extents, 1,000,000 decoded runs, and 16 MiB aggregate mapping-pairs bytes. These are parser resource limits, not a claim that continuation discovery is complete.

## Safety model

- Partition inspection and lost-partition scanning currently open regular image files only. The separate experimental physical reader is not connected to those workflows.
- Source reads are bounded; the image reader exposes no write API.
- Physical-disk acquisition is a guarded, Windows-only, all-or-nothing image copy. It requires complete volume and boot maps, excludes identified boot-critical disks, requires a unique reported serial and supported non-layered bus identity, revalidates source identity/geometry, and verifies that the destination volume is physically separate. The source handle requests read access only. A read error aborts acquisition; unreadable sectors are never silently zero-filled. The image is length/SHA-256 verified before publication, with a sidecar JSON manifest.
- This is not a hardware write-blocked forensic acquisition. A read-only raw handle does not prevent Windows or another process from writing through mounted filesystems, and the copy is not a point-in-time snapshot. A hardware write blocker is strongly recommended for irreplaceable/evidentiary media. Resume, bad-sector maps, retries, and desktop integration are not yet implemented. Do not use this experimental acquisition path on the only copy of valuable data until it has passed disposable virtual-disk and physical-hardware validation.
- Physical partition scanning and recovery from a live device remain disabled. No mounting, repair, formatting, initialization, or repartitioning is implemented.
- JSON reports are written only to a user-selected report path.
- File-data extraction is image-file-only. Deleted-file extraction is a separate opt-in path: it requires verified deleted base/continuation MFT metadata and current `$Bitmap` evidence that every cluster in the assembled resident-`$ATTRIBUTE_LIST` stream map is free. A free bit is not proof that bytes were never overwritten. The CLI refuses source-path overwrite and existing output/manifest files, writes to temporary files, and publishes only after a complete bounded read and output hash calculation. On Windows it resolves the source image volume and destination volume to physical disk extents, requires complete mappings and distinct identified non-layered physical disks with unique reported serials, checks destination capacity, and repeats the separation check before publication. Unknown, virtual, RAID, Storage Spaces, network, reparse-point, or otherwise unmappable storage fails closed. This gate still requires Windows CI and manual disposable-volume validation.
- The deleted-data output SHA-256 identifies the exported bytes only; it is not an original-file hash or a content-integrity verdict. The tool reports content integrity as unverified. The CLI compares source-image length and last-write timestamp before publication and aborts if either changes; this is a best-effort metadata check, not a stable snapshot guarantee, and cannot rule out writes through a handle that was already open.
- All automated integration checks use generated disposable images; the original HDD is not used for tests.
- Deleted-image export requires a new destination on a verified separate physical disk; same-disk, unknown, layered, or incomplete storage mappings are refused. Use separate storage for valuable recovery work.

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

The destination directory must already exist and the destination file must not exist. The command reports progress and the resulting SHA-256; Ctrl+C cancels and removes incomplete temporary output when possible. This command accepts regular image files only. The experimental Windows physical-acquisition workflow is available through the desktop button “Physical disks / acquire image…” and the CLI commands below. It has not been validated on physical hardware.

Experimental Windows physical acquisition (not yet hardware-validated):

```powershell
dotnet run --project src/DiskRescue.Cli -- list-physical-disks
dotnet run --project src/DiskRescue.Cli -- acquire-physical 2 "EXACT-REPORTED-SERIAL" "D:\DiskRescue\physical-drive-2.img"
```

The destination directory must already exist and be on a verified separate physical disk. The command repeats source and destination preflight, displays the selected device identity and warnings, and requires typing the exact phrase ACQUIRE PHYSICALDRIVE2 before opening the source. It aborts on read errors without zero-filling and publishes a verified image plus a JSON sidecar manifest. This is a live, non-resumable acquisition—not a hardware write-blocked forensic image or a point-in-time snapshot. Do not use it on the only copy of valuable data before disposable virtual-disk and Windows hardware validation. Prefer a hardware write blocker for irreplaceable media.

Search a regular disk-image file for NTFS volumes missing from its MBR/GPT partition tables:

```powershell
dotnet run --project src/DiskRescue.Cli -- discover-ntfs "C:\Images\sample.img" --max-scan-bytes 4294967296
```

The scanner reads the image only, checks NTFS boot-sector geometry and the backup boot sector, and reports candidate offsets and confidence evidence. The default scan limit is 4 GiB; increase it explicitly for larger images. It does not scan a live physical disk or modify partition tables.

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

Opt-in deleted NTFS stream export (preflight-gated; content integrity remains unverified):

```powershell
dotnet run --project src/DiskRescue.Cli -- extract-deleted-ntfs-file "C:\Images\sample.img" --offset 1048576 --length 1073741824 --record 42 --output "D:\Recovered\deleted-record-42.bin" --max-bytes 268435456
```

The command supports resident streams and non-resident multi-extent streams only when a resident deleted-file `$ATTRIBUTE_LIST` fully validates the base and continuation FILE records, and every continuation is reachable through the initially validated MFT map. It verifies deleted FILE-record and parent identity, exact attribute identities, contiguous VCN coverage, non-overlapping physical runs, and current `$Bitmap` evidence for every referenced cluster. The complete preflight is repeated immediately before reading. Non-resident `$ATTRIBUTE_LIST` bootstrap, recursive MFT discovery, sparse, compressed, and encrypted streams remain unsupported. The command zero-fills bytes beyond initialized size, enforces the explicit byte ceiling, writes to temporary files, and never overwrites an existing output or manifest. Before creating output and again before publishing, it verifies the source image's backing disk(s) and destination backing disk(s) are distinct, identifiable physical devices with complete volume mappings and supported bus identities. This is Windows-only and fails closed when that cannot be established. A free-cluster result cannot establish overwrite history; the output SHA-256 is only a hash of the exported bytes. Content integrity is always reported as unverified.

A successful export also creates a sidecar `<output-file>.recovery.json` manifest. It records source path/name/length and timestamps, volume boundaries and sector size, deleted MFT record and stream, metadata and allocation assessments, stream length and initialized size, cluster size, the validated physical run map (VCN/LCN ranges), exported byte count and SHA-256, safety flags, and warnings. The manifest explicitly says the export hash is not an original-file hash and that content integrity and physical-device recovery are unverified. The CLI refuses to overwrite an existing output or manifest and removes the output if it cannot publish the matching manifest.

Run the complete disposable end-to-end checklist locally:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-DeletedNtfsRecovery.ps1
```

The harness builds the CLI and validates successful export and manifest contents, source-image immutability, refusal of allocated clusters, byte-limit enforcement, source-path and existing-file protection, manifest collision handling, and temporary-file cleanup. It uses generated synthetic images only.

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
3. Extend multi-extent stream handling to additional live-file layouts; add sparse-file handling, compressed/encrypted stream diagnostics, and stronger recovered-content integrity verification.
4. Extend image-based lost-partition discovery to FAT32 and exFAT, improve candidate deduplication/conflict analysis, and expand generated regression fixtures. The current scanner is NTFS-only and does not access physical disks.
5. Deleted-stream CLI end-to-end validation covers resident/single-extent and synthetic fragmented multi-extent image workflows, with manifest run-map provenance. Destination separation is enforced by a Windows-only fail-closed preflight and a second pre-publication check; unit tests cover physical disk identity, bus type, serial uniqueness, and mapping failures. CI run 42513427158 passed the full successful export and packaging path on a runner exposing disk identities. Runners without unique serial metadata or with virtualized/layered device descriptions correctly refuse export; the harness reports those environment limitations instead of weakening the production gate. Injected disk-full, permission, output-publication, manifest-publication, and rollback failures are covered. Remaining: abrupt-termination cleanup, resume semantics, broader real-world NTFS corpus coverage, and manual Windows desktop validation.
6. Finish the physical acquisition release gate: add durable bad-sector/error reporting and a safe resume journal, validate cancellation and manifest-failure recovery, and test the guarded workflow against sacrificial physical hardware. The experimental WPF acquisition window is integrated; physical partition scanning remains disabled until the remaining checks pass.

See [docs/ROADMAP.md](docs/ROADMAP.md) for status and release gates.

## Contribution rules

Every feature must include automated tests, documented limitations, safe failure behavior, and source-write prevention. Never upload real user disk images, recovered personal files, credentials, or sensitive data to issues or pull requests.

## License

The project license will be finalized after dependency and distribution review.
