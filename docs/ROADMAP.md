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
- [ ] Lost-partition candidate discovery and confidence scoring
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
- [ ] Read resident and non-resident `$ATTRIBUTE_LIST` values safely
- [ ] Resolve continuation records with sequence/base-reference validation
- [ ] Assemble additional MFT extents with exact VCN continuity and cycle/resource limits
- [ ] Add fragmented, multi-record, cyclic, stale-reference, and malicious attribute-list fixtures
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
- [ ] Guided lost-partition discovery with evidence/confidence panels
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

The post-merge Windows CI run [42095792708](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42095792708) completed successfully on `main`: 53 tests passed, generated MBR/GPT/NTFS fixture generation and CLI smoke tests passed, WPF built, and self-contained Windows x64 CLI and desktop artifacts were published.

The current feature branch adds the first `$ATTRIBUTE_LIST` entry parser and dedicated bounds tests. Windows CI run [42097467414](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42097467414) passed all 60 tests, generated-image CLI smoke tests, WPF build, and Windows x64 desktop/CLI publishing. Manual Windows desktop interaction is still outstanding.
