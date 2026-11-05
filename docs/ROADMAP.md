# Roadmap

## Phase 0 — Foundation

- [x] Define safety-first project charter and data-preservation policy
- [x] Add .NET 10 solution, layered projects, and Windows CI
- [x] Add reusable synthetic disk-image fixtures

## Phase 1 — Read-only image inspection

- [x] Bounded read-only random access for regular image files
- [x] MBR parsing with signature, entry, bounds, sector-size, protective-MBR, and overlap checks
- [x] GPT primary/backup parsing with header and array CRC32, bounds, and copy-consistency checks
- [x] Fall back to a validated backup GPT copy when the primary header is damaged
- [x] CLI JSON inspection report
- [x] Initial WPF image-inspection UI and JSON report export
- [x] Generate disposable MBR/GPT images for repeatable manual testing
- [x] Windows CI build, parser tests, and self-contained x64 preview publishing
- [ ] Manual Windows desktop smoke test with generated images
- [ ] Disk and volume inventory with device identity and geometry (future read-only milestone)

## Phase 2 — Lost-partition discovery

- [ ] Bounded search for filesystem boot-sector signatures
- [ ] Generate partition candidates without modifying source media
- [ ] Score candidates using independent metadata checks and explain confidence
- [ ] Compare candidate evidence against primary and backup partition metadata

## Phase 3 — Filesystem inspection

- [x] Initial NTFS boot-sector validation: OEM/signature, sector and cluster geometry, volume bounds, MFT/MFT mirror locations, and record-size encoding
- [x] NTFS FILE record header validation and update-sequence-array fixup validation
- [x] Bounded resident/non-resident attribute-header parsing with value, name, and mapping-pairs range checks
- [x] NTFS mapping-pairs decoder for fragmented and sparse extents with VCN/LCN bounds
- [ ] Integrate validated MFT record reading through decoded extents
- [ ] NTFS attribute and runlist integration with strict bounds
- [ ] Read-only directory and file listing
- [ ] FAT32 and exFAT boot-sector and metadata parsers
- [ ] Parser fuzzing and malformed-image regression suite

## Phase 4 — Safe recovery

- [ ] Recovery preview and file-integrity checks
- [ ] Enforce source/destination separation
- [ ] Extract recovered files to a separate destination only
- [ ] Recovery manifest, hashes, and audit log
- [ ] Interrupted extraction and collision handling

## Phase 5 — Desktop experience

- [x] Initial WPF image selection, MBR/GPT inspection, partition overview, findings, and JSON export
- [ ] Guided lost-partition scan workflow with clear warnings
- [ ] Evidence view and confidence explanations
- [ ] Accessible progress, cancellation, and recovery reports

## Current implementation boundary

- `DiskRescue.IO` supports regular image files only; physical devices are not opened.
- `DiskRescue.Partitions` validates MBR and GPT metadata. A validated backup GPT copy can be used when the primary GPT header is damaged.
- `DiskRescue.Filesystems` validates NTFS boot-sector geometry, FILE record USA fixups, bounded attribute headers, and mapping-pairs runlists. These are parser building blocks only: the application does not yet traverse the MFT, list directories, follow attribute lists, decode indexes, or recover file contents.
- The desktop UI and CLI currently expose partition inspection. The NTFS validator is a library milestone and is not yet wired into a user workflow.
- The PowerShell test-image generator creates three disposable fixtures: MBR, healthy GPT, and GPT with a damaged primary header but valid backup copy. CI generates these fixtures and smoke-tests CLI behavior end to end. NTFS record, attribute, and runlist parsers have dedicated synthetic unit tests.

## Validation and release gates

Windows CI run 42083397605 passed solution restore/build, all 28 automated tests, WPF build, and Windows x64 CLI and desktop preview publishing at commit 1ca82a6bbcf651ed709fc2ba4479aa786719d45e. The newer CI workflow at the current feature-branch head adds fixture-generation and CLI smoke-test steps; its result must pass before treating that workflow change as validated. Manual Windows desktop interaction is still required.

A feature is not complete until it has automated tests, documented limitations, safe failure behavior, and evidence that source media cannot be modified. No destructive repair feature is included in this roadmap.
