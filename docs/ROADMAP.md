# Product Roadmap

DiskRescue Pro is being delivered in safety-gated increments. A roadmap checkbox means the behavior exists and has automated evidence; it does not imply complete filesystem or device coverage.

## Phase 0 — Engineering foundation

- [x] Define data-preservation and read-only principles
- [x] Create .NET 10 solution and Windows CI
- [x] Add generated disposable disk-image fixtures
- [x] Publish self-contained Windows x64 CLI and WPF preview artifacts

## Phase 1 — Image and partition inspection

- [x] Bounded read-only random access for regular image files
- [x] MBR signature, entry, bounds, geometry, protective-entry, and overlap validation
- [x] GPT primary/backup parsing and independent header/array CRC validation
- [x] Validated backup-GPT fallback when primary header is damaged
- [x] CLI structured JSON inspection report
- [x] WPF image selection, partition overview, findings, and JSON export
- [x] Generated MBR/GPT images and CLI smoke tests
- [ ] Manual Windows desktop smoke test recorded against generated MBR/GPT images
- [x] Bounded, chunked NTFS boot-sector discovery in image files with primary BPB validation, backup-sector corroboration, progress, cancellation, and candidate evidence\n- [x] Classify NTFS candidates against parsed MBR/GPT ranges as existing, overlapping, or lost; add lost candidates to the desktop partition list for bounded NTFS preview\n- [ ] FAT32 and exFAT lost-partition candidate discovery and confidence scoring\n- [ ] Generated integration fixtures for lost NTFS candidates and desktop discovery flow
- [ ] Physical disk inventory and acquisition (separate future safety gate)

## Phase 2 — NTFS metadata preview

- [x] NTFS boot-sector geometry and volume-boundary validation
- [x] FILE record signature, bounds, and update-sequence-array fixup validation
- [x] Bounded resident/non-resident attribute-header parsing
- [x] NTFS mapping-pairs decoding with sparse-run and volume-cluster bounds
- [x] MFT record-0 validation and initial unnamed `$MFT::$DATA` mapping
- [x] Read MFT records across fragmented physical runs within initial extent
- [x] Resident `$FILE_NAME` parsing and parent-reference extraction
- [x] Bounded flat scan of initialized records in initial MFT extent
- [x] Conservative partial tree with parent sequence checks, cycle detection, and orphan reporting
- [x] CLI NTFS scan and WPF selected-partition NTFS preview
- [x] Bounded binary parser for `$ATTRIBUTE_LIST` entries
- [x] Parse resident `$ATTRIBUTE_LIST` values from validated attributes; reject non-resident list streams as unsupported
- [x] Match parsed entries against supplied validated records by record sequence, base reference, type, ordinal UTF-16 name, attribute ID, and lowest VCN
- [x] Return structured diagnostics for stale, mismatched, duplicate, ambiguous, malformed, and unsupported references
- [ ] Read referenced records outside the initial validated MFT extent through a safely bootstrapped validated mapping
- [x] Add in-memory MFT extent-map assembly for caller-validated attributes with exact VCN continuity, runlist-length checks, and volume bounds
- [ ] Integrate assembled extent maps into MFT record reads
- [ ] Resolve resident and non-resident `$ATTRIBUTE_LIST` entries recursively with cycle/resource limits
- [x] Add synthetic-record tests for mismatched, duplicate, ambiguous, malformed, stale, and limit-exceeding references
- [ ] Add generated-image integration fixtures for multi-record continuation and extent assembly
- [ ] Parse `$INDEX_ROOT` and `$INDEX_ALLOCATION`
- [ ] Validate complete parent relationships and build a reliable tree
- [ ] Add NTFS compression/encryption/reparse-point semantics and explicit unsupported diagnostics

## Phase 3 — Lost-partition discovery

- [ ] Bounded filesystem-signature search over image ranges
- [ ] Candidate generation for NTFS, FAT32, and exFAT
- [ ] Independent boot-sector and backup-boot-sector validation
- [ ] Candidate overlap and conflict analysis
- [ ] Evidence-based confidence explanations (never silent repair)
- [ ] FAT32 and exFAT metadata parsers with generated regression fixtures

## Phase 4 — Read-only file-content mapping

- [ ] Resolve unnamed and named `$DATA` attributes for files
- [ ] Support fragmented files with strict VCN/LCN and logical-size checks
- [ ] Handle sparse, compressed, encrypted, and resident content safely (or report unsupported)
- [ ] Detect hard links, alternate data streams, and reparse points
- [ ] Verify extracted candidate length and hash
- [ ] Keep file listing separate from claims of recoverability

## Phase 5 — Safe extraction

- [ ] Enforce source/destination physical identity separation
- [ ] Canonicalize destination paths and prevent traversal/reparse-point escape
- [ ] Never overwrite existing destination files by default
- [ ] Stream with bounded buffers and short-write handling
- [ ] Support cancellation and safe resume of interrupted extraction
- [ ] Export manifest, hashes, source offsets, diagnostics, and audit log
- [ ] Test disk-full, permission failures, collisions, and interrupted writes

## Phase 6 — Professional desktop experience

- [x] Initial WPF dashboard and partition inspection
- [x] NTFS partial tree preview
- [ ] Device/image acquisition wizard with explicit read-only identity checks
- [x] Guided NTFS lost-partition discovery with evidence/confidence panel, progress, cancellation, and selectable candidates (image files only)\n- [ ] Expand discovery UI to FAT32/exFAT and add deeper candidate comparison tools
- [ ] Search, filter, sort, and metadata inspection for file entries
- [ ] Accessible progress, cancellation, and clear failure states
- [ ] Recovery destination verification and extraction queue
- [ ] Signed installer, versioning, release notes, and reproducible release workflow
- [ ] Manual usability, accessibility, and Windows-version testing

## Non-negotiable release gates

- Every parser change includes positive, negative, boundary, and adversarial tests.
- Every read is bounded by the supplied image/volume and uses checked arithmetic.
- Source write capability is not introduced into the inspection interfaces.
- Recovery output is written only to a verified separate destination.
- No destructive repair, format, initialize, or repartition feature is planned for the initial product.
- No physical-device support ships without a dedicated read-only handle design, device identity validation, threat review, and separate test suite.

## Current validation baseline

The live/lost NTFS image scanner is currently under validation on `feat/live-lost-partition-scan`; its unit tests and Windows desktop integration must pass on the exact branch head before this phase is considered complete.\n\nThe post-merge Windows CI run [42095792708](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42095792708) completed successfully on `main`: 53 tests passed, generated MBR/GPT/NTFS fixture generation and CLI smoke tests passed, WPF built, and self-contained Windows x64 CLI and desktop artifacts were published.

Windows CI run [42097467414](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42097467414) validated the earlier parser-only feature (60 tests, generated-image CLI smoke tests, WPF build, and Windows x64 desktop/CLI publishing). The current bounded matcher is covered by additional synthetic-record unit tests; its full-solution CI and packaging validation remain release gates. Manual Windows desktop interaction is still outstanding.
