# Roadmap

## Phase 0 — Foundation

- [x] Create project repository and safety-first project charter
- [x] Define architecture boundaries and data-preservation policy
- [x] Add initial .NET solution, project structure, and Windows CI workflow
- [x] Add reusable synthetic disk-image builder and baseline MBR/GPT fixtures

## Phase 1 — Read-only inspection

- [x] Add bounded, read-only image-file random access (regular image files only; physical-device namespace paths and reparse points rejected)
- [x] Add initial CLI command to inspect an image and emit a structured JSON report
- [x] Document first-version preview usage and safety limits
- [x] Publish a self-contained Windows x64 CLI artifact (CI run 70); runtime smoke test still pending
- [ ] Disk and volume inventory (Windows)
- [ ] Device identity and sector geometry reporting
- [x] Add initial MBR parsing with signature, boot-indicator, sector-size, range, protective-MBR, and overlap diagnostics
- [x] Add initial GPT primary/backup parsing with header and partition-array CRC32 validation, bounded entry arrays, image-bound checks, and copy-consistency diagnostics
- [x] Expand synthetic parser coverage for primary/backup GUID mismatch, truncated images, and 4Kn logical sectors

## Phase 2 — Lost partition discovery

- [ ] Search for filesystem boot-sector signatures in bounded ranges
- [ ] Generate partition candidates without modifying media
- [ ] Score candidates using multiple independent metadata checks
- [ ] Explain confidence and ambiguity to the user

## Phase 3 — Filesystem inspection

- [ ] NTFS read-only metadata inspection
- [ ] FAT32 and exFAT read-only metadata inspection
- [ ] Safe directory and file listing where structures are consistent
- [ ] Corruption handling and parser fuzzing

## Phase 4 — Safe recovery

- [ ] Recovery preview and file integrity checks
- [ ] Destination validation and source/destination separation
- [ ] Extraction to a separate disk
- [ ] Recovery manifest and audit log

## Phase 5 — Desktop experience

- [x] Initial WPF desktop shell for image selection and MBR/GPT inspection
- [x] Partition overview, validation findings, and JSON report export
- [ ] Guided scan workflow with clear risk warnings
- [ ] Physical disk/image identity confirmation (physical disks remain unsupported)
- [ ] Evidence view for lost-partition candidates
- [ ] Accessible progress, cancellation, and reports

## Current implementation notes

- `DiskRescue.IO` reads regular image files only; it exposes no physical-device access or source-write API. Device namespace paths, directories, and reparse points are rejected.
- `DiskRescue.Partitions` contains initial MBR and GPT parsers. MBR parsing requires the 0x55AA signature. GPT parsing validates revision, header CRC, partition-array CRC, standard primary/backup placement, bounded entry counts/sizes, image bounds, usable-LBA ranges, duplicate GUIDs, overlaps, and primary/backup metadata consistency.
- Tests use a shared reusable synthetic-image builder and disposable temporary files; fixtures cover MBR validation, GPT primary/backup copies, CRC failures, overlaps, entry-count limits, primary/backup GUID mismatch, truncated images, and 4Kn sector geometry. Earlier Windows CI runs 21 and 37 passed the image-reader/MBR and original GPT milestones. New validation for the shared builder and expanded cases is running on the current feature-branch head.
- The CLI is an engineering preview, not a finished end-user desktop product. The CI workflow now attempts to publish a self-contained Windows x64 preview artifact; verify that artifact only after the publish job succeeds. Physical-disk inventory, lost-partition discovery, filesystem parsing, and recovery are not implemented. The WPF desktop shell is an initial image-inspection UI and still requires Windows CI validation and hands-on usability testing.

## Release gates

A feature is not complete until it has automated tests, documented limitations, safe failure behavior, and evidence that it cannot write to source media. No destructive repair feature is included in the initial roadmap.
