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
- [x] CLI JSON partition-inspection report
- [x] Initial WPF image selection, MBR/GPT inspection, partition overview, findings, and JSON export
- [x] Generate disposable MBR/GPT images for repeatable manual testing
- [x] Windows CI build, parser tests, fixture smoke tests, and self-contained x64 preview publishing
- [ ] Manual Windows desktop smoke test with generated images
- [ ] Disk and volume inventory with device identity and geometry (future read-only milestone)

## Phase 2 — Lost-partition discovery

- [ ] Bounded search for filesystem boot-sector signatures
- [ ] Generate partition candidates without modifying source media
- [ ] Score candidates using independent metadata checks and explain confidence
- [ ] Compare candidate evidence against primary and backup partition metadata

## Phase 3 — Filesystem inspection

- [x] NTFS boot-sector validation: OEM/signature, sector and cluster geometry, volume bounds, MFT pointer bounds, and record-size encoding
- [x] NTFS FILE record header validation and update-sequence-array fixup validation
- [x] Bounded resident/non-resident attribute-header parsing with value, name, and mapping-pairs range checks
- [x] NTFS mapping-pairs decoder for fragmented and sparse extents with VCN/LCN bounds
- [x] Read MFT FILE records through the validated initial $MFT::$DATA extent, including records crossing fragmented physical runs
- [x] Parse resident $FILE_NAME values and parent file references
- [x] Bounded flat scan of initialized MFT records covered by the initial extent
- [x] CLI command for bounded read-only NTFS MFT preview (latest CLI integration is awaiting CI validation)
- [ ] Resolve additional MFT extents through $ATTRIBUTE_LIST with VCN continuity and cycle checks
- [ ] Validate parent record sequence references and build a read-only directory/file tree
- [ ] Parse $INDEX_ROOT and $INDEX_ALLOCATION directory indexes
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
- [ ] Guided NTFS MFT scan workflow in WPF
- [ ] Guided lost-partition scan workflow with clear warnings
- [ ] Evidence view and confidence explanations
- [ ] Accessible progress, cancellation, and recovery reports

## Current implementation boundary

- `DiskRescue.IO` supports regular image files only; physical devices are not opened.
- `DiskRescue.Partitions` validates MBR and GPT metadata. A validated backup GPT copy can be used when the primary GPT header is damaged.
- `DiskRescue.Filesystems` validates NTFS boot-sector geometry, FILE record USA fixups, bounded attribute headers, mapping-pairs runlists, and resident $FILE_NAME values. `NtfsMftReader` reads records through the initial validated $MFT::$DATA extent and can perform a bounded flat scan. It does not resolve additional extents through $ATTRIBUTE_LIST, validate a complete parent hierarchy, traverse directory indexes, or recover file contents.
- The CLI has a `scan-ntfs` command that accepts an image file and explicit volume byte offset/length. CI run 42090410822 passed the synthetic NTFS image generation and end-to-end CLI smoke test; the subsequent sector-size consistency guard is being revalidated in run 42090536737.
- The WPF desktop currently exposes partition inspection only; the NTFS scan workflow is not yet wired into the desktop UI.
- The PowerShell test-image generator creates disposable MBR, healthy GPT, and damaged-primary-GPT/valid-backup fixtures. CI generates these fixtures and smoke-tests CLI behavior. NTFS parsers, fragmented MFT reads, filename parsing, and bounded scanning have synthetic unit tests.

## Validation and release gates

Windows CI run 42089190267 passed restore, build, all automated tests, generated-image CLI smoke tests, WPF build, and Windows x64 preview publishing at commit 71d0be8b3ac0b0fc76af653ae1dc798f426a1a2f. A subsequent successful run 42089091231 also validated the bounded MFT scan implementation at commit 22a22fd38a77f5cca6ee7ce0b3d9d1aa37a4e7c7. CI run 42090410822 also passed the new synthetic NTFS MFT CLI smoke test; run 42090536737 is validating the subsequent sector-size guard and current branch head. Manual Windows desktop interaction is still required.

A feature is not complete until it has automated tests, documented limitations, safe failure behavior, and evidence that source media cannot be modified. No destructive repair feature is included in this roadmap.
