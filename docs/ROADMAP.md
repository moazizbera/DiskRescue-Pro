# Roadmap

## Phase 0 — Foundation

- [x] Create project repository and safety-first project charter
- [x] Define architecture boundaries and data-preservation policy
- [x] Add initial .NET solution, project structure, and Windows CI workflow
- [ ] Add reusable synthetic disk-image builder and baseline fixtures (current tests generate temporary MBR/GPT images directly)

## Phase 1 — Read-only inspection

- [x] Add bounded, read-only image-file random access (regular image files only; physical-device namespace paths and reparse points rejected)
- [ ] Disk and volume inventory (Windows)
- [ ] Device identity and sector geometry reporting
- [x] Add initial MBR parsing with signature, boot-indicator, sector-size, range, protective-MBR, and overlap diagnostics
- [x] Add initial GPT primary/backup parsing with header and partition-array CRC32 validation, bounded entry arrays, image-bound checks, and copy-consistency diagnostics (latest CI validation pending)

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

- [ ] Guided scan workflow with clear risk warnings
- [ ] Disk/image selection and identity confirmation
- [ ] Evidence view for partition candidates
- [ ] Accessible progress, cancellation, and reports

## Current implementation notes

- `DiskRescue.IO` reads regular image files only; it exposes no physical-device access or source-write API. Device namespace paths, directories, and reparse points are rejected.
- `DiskRescue.Partitions` contains initial MBR and GPT parsers. MBR parsing requires the 0x55AA signature. GPT parsing validates revision, header CRC, partition-array CRC, header placement, bounded entry counts/sizes, image bounds, usable-LBA ranges, duplicate GUIDs, overlaps, and primary/backup metadata consistency.
- Tests use generated temporary image files, including synthetic primary/backup GPT copies. The latest GPT parser and test changes require a successful Windows CI run before they can be considered validated.
- Physical-disk inventory, lost-partition discovery, filesystem parsing, recovery, and the desktop application are not implemented.

## Release gates

A feature is not complete until it has automated tests, documented limitations, safe failure behavior, and evidence that it cannot write to source media. No destructive repair feature is included in the initial roadmap.
