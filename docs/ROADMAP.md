# Roadmap

## Phase 0 — Foundation

- [x] Create project repository and safety-first project charter
- [x] Define architecture boundaries and data-preservation policy
- [ ] Add solution, project structure, formatting, and CI
- [ ] Add synthetic disk-image builder and baseline fixtures

## Phase 1 — Read-only inspection

- [ ] Read-only image abstraction with bounded random access
- [ ] Disk and volume inventory (Windows)
- [ ] Device identity and sector geometry reporting
- [ ] MBR parsing with validation and diagnostics
- [ ] GPT primary/backup parsing, CRC checks, and disagreement reporting

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

## Release gates

A feature is not complete until it has automated tests, documented limitations, safe failure behavior, and evidence that it cannot write to source media. No destructive repair feature is included in the initial roadmap.
