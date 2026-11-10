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
- [x] Bounded, chunked NTFS boot-sector discovery in image files with primary BPB validation, backup-sector corroboration, progress, cancellation, and candidate evidence
- [x] Classify NTFS candidates against parsed MBR/GPT ranges as existing, overlapping, or lost; add lost candidates to the desktop partition list for bounded NTFS preview
- [x] Bounded FAT32/exFAT candidate scanning in regular image files, primary/backup validation, MBR/GPT overlap classification, evidence-based confidence, and synthetic regression coverage (CLI: `discover-fat`)
- [x] Integrate FAT32/exFAT candidates into the WPF discovery workflow, findings panel, and partition candidate list (automated build/test; manual interactive validation remains open)
- [ ] Add deeper candidate comparison and conflict-resolution UI
- [x] Generated-image integration fixtures for lost NTFS candidate discovery, bounded preview, and CLI extraction; desktop interaction remains a separate manual gate
- [~] Experimental Windows physical-disk inventory and guarded image acquisition coordinator/CLI; WPF acquisition window is integrated; hardware validation remains a release gate

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

- [x] Bounded filesystem-signature search over regular image-file ranges for NTFS, FAT32, and exFAT
- [x] Candidate generation for NTFS, FAT32, and exFAT
- [x] Independent boot-sector and backup-boot-sector validation where supported by each filesystem
- [x] Candidate overlap and conflict analysis against parsed MBR/GPT ranges
- [x] Evidence-based confidence explanations (never silent repair)
- [x] Conservative FAT32 BPB and exFAT main/backup boot-region validators with bounded geometry checks and synthetic regression tests
- [x] FAT32 and exFAT lost-partition candidate scanning, overlap classification, and confidence scoring

## Phase 4 — Read-only file-content mapping

- [ ] Resolve unnamed and named `$DATA` attributes for files
- [ ] Support fragmented files with strict VCN/LCN and logical-size checks
- [ ] Handle sparse, compressed, encrypted, and resident content safely (or report unsupported)
- [ ] Detect hard links, alternate data streams, and reparse points
- [x] Verify bounded exported byte count and calculate a SHA-256 for the exported bytes (not an original-file integrity check)
- [x] Keep deleted-file metadata/allocation eligibility separate from content-integrity claims; the current export always reports integrity unverified
- [x] Assemble deleted multi-extent streams through a deleted-specific validated `$ATTRIBUTE_LIST` matcher; validate VCN continuity, physical-run overlap, and allocation across every extent (content integrity remains unverified)

## Phase 5 — Safe extraction

- [x] Refuse source-image overwrite and verify deleted-image export destination resolves to physical disk(s) separate from every disk backing the source image; fail closed on incomplete/unmappable identities and recheck before publication
- [ ] Canonicalize destination paths and prevent traversal/reparse-point escape for future filename/tree recovery
- [x] Never overwrite existing deleted-export destination or manifest files
- [x] Copy supported streams with bounded buffers, caller byte ceilings, initialized-size handling, and volume/image bounds
- [~] Support cancellation and temporary-file cleanup; resumable extraction remains outstanding
- [x] Export a JSON sidecar manifest with source identity metadata, volume boundaries, MFT identity, allocation evidence, output SHA-256, diagnostics, and explicit integrity/safety status
- [x] Test synthetic deleted-export success, allocated-cluster refusal, byte ceilings, source immutability, existing-output/manifest collisions, and temporary-file cleanup
- [x] Injected output-publication, manifest-publication, rollback, disk-full-write, and destination-permission failures are covered
- [~] Use an exclusive per-export operation lock and exact GUID-scoped temporary names; a later invocation removes abandoned temporary artifacts only after acquiring the stale lock exclusively and validating its marker. Active, malformed, or inaccessible locks are preserved. If the output partial is missing while the manifest partial remains, preserve both the manifest partial and lock because the final output may already have been moved. Automated tests cover abandoned-operation cleanup, active-operation protection, malformed markers, interrupted-publication preservation, and normal disposal.
- [x] Add a child-process termination test for an exporter lease owner: forcibly terminate the child after it creates GUID-scoped partial output/manifest files, then verify a later cleanup safely removes only those owned partial artifacts.
- [x] Child-process termination at the critical final-publication gap: terminate after the output is moved but before the manifest is published; verify the final output, pending manifest partial, and operation lock are all preserved and stale cleanup does not delete them. Recovery/resume of that orphan remains a separate pending capability.
- [x] Add explicit interrupted-export finalization: require the matching exclusive operation lock and versioned manifest, constrain the output path to the pending manifest directory, verify exact output length and SHA-256, refuse reparse points and existing final manifests, and preserve all evidence on any mismatch. This finalizes the sidecar only; it does not resume source reads or prove original content integrity.
- [x] Propagate cancellation during `$Bitmap` preflight as `OperationCanceledException`; regression coverage verifies cancellation is not downgraded to a generic allocation refusal.
- [x] Treat CI runners without unique serial metadata or with virtualized/layered device descriptions as unsupported for the positive export E2E path; the production gate remains fail-closed, and the harness skips only this environment-dependent success path.
- [x] Add deleted-image export destination-volume identity checks; source and destination physical disk mappings must be complete and non-overlapping before export and again before publication

## Phase 6 — Professional desktop experience

- [x] Initial WPF dashboard and partition inspection
- [x] NTFS partial tree preview
- [x] Experimental physical-image acquisition window with read-only identity checks, destination preflight, explicit confirmation, progress, and cancellation (hardware validation remains a release gate)
- [x] Guided NTFS lost-partition discovery with evidence/confidence panel, progress, cancellation, and selectable candidates (image files only)
- [x] Display discovered FAT32/exFAT candidates in the WPF discovery UI and findings panel (automated integration/build validation complete)
- [ ] Add deeper candidate comparison tools
- [ ] Search, filter, sort, and metadata inspection for file entries
- [~] Accessible progress, cancellation, and clear failure states — named controls, keyboard focus cues, and polite live status regions implemented on `feat/desktop-accessibility-focus-states`; exact-head CI and manual keyboard/screen-reader validation pending.
- [ ] Recovery destination verification and extraction queue
- [ ] Signed installer, versioning, release notes, and reproducible release workflow
- [ ] Manual usability, accessibility, and Windows-version testing

## Deleted NTFS bounded-export milestone (image files only)

The PR #9 feature branch includes a separate deleted-stream export command gated by verified deleted base FILE-record/parent identity and current `$Bitmap` evidence. It supports resident streams and non-resident multi-extent streams when a resident `$ATTRIBUTE_LIST` completely identifies the deleted base and continuation FILE records, and those records are reachable through the initially validated MFT map. The deleted-specific matcher checks record sequence, base reference, attribute type/name/ID/lowest VCN; the stream assessor enforces contiguous VCN coverage, valid mapping pairs, non-overlapping physical runs, and currently-free allocation across every extent. The exporter re-runs the complete preflight immediately before content reads.

The recovery manifest records stream size, initialized size, cluster size, and the validated physical run map, alongside the output SHA-256. That hash identifies exported bytes only. A free allocation bit does not prove the bytes were never overwritten, so content integrity is always reported as unverified. Sparse, compressed, encrypted, non-resident `$ATTRIBUTE_LIST` bootstrap, recursive MFT discovery, and physical-device partition recovery remain unsupported.

The dedicated `tools/Test-DeletedNtfsRecovery.ps1` harness generates disposable synthetic images and checks single-extent and physically fragmented multi-extent exports, exact payload bytes, manifest run-map provenance, source-image immutability, allocation refusal including a continuation-only allocated cluster, byte ceilings, collision protections, and temporary-file cleanup. Matcher unit tests also cover stale continuation sequences, incorrect base references, duplicate entries, and active continuation records. This does not validate physical HDD recovery, and the original HDD is not used.

## Non-negotiable release gates

- Every parser change includes positive, negative, boundary, and adversarial tests.
- Every read is bounded by the supplied image/volume and uses checked arithmetic.
- Source write capability is not introduced into the inspection interfaces.
- Physical image acquisition and deleted-image export require a verified physically separate destination. Deleted-image export resolves the source image volume and destination volume to physical disk extents, rejects any overlap or incomplete mapping, checks destination capacity, and repeats the check before publishing output. This is a Windows-only gate; unknown, network, virtual, reparse-point, or otherwise unmappable storage fails closed.
- No destructive repair, format, initialize, or repartition feature is planned for the initial product.
- No physical-device support is considered release-ready without a dedicated read-only handle design, device identity validation, complete source/destination preflight, hardware write-blocker guidance, bad-sector/error policy, cancellation and failure recovery, threat review, disposable virtual-disk tests, and representative Windows hardware validation.

## Physical acquisition status (experimental)

The current feature branch includes a guarded all-or-nothing physical image acquisition coordinator, an experimental WPF acquisition window, and CLI entry points. It performs two source/destination preflights, excludes identified boot-critical disks, checks reported serial uniqueness and bus/removable status, verifies destination free space and FAT/FAT32 file-size limits with a 64 KiB manifest/filesystem reserve, opens the source read-only, aborts on read errors without zero-fill, verifies image length and SHA-256 before publication, and writes a sidecar manifest. Windows CI validates compilation and unit-level preflight decisions. It does not yet provide resumable acquisition, bad-sector maps/retries, or physical hardware validation. The desktop window has not yet been exercised against a disposable virtual disk on Windows. Do not use it on the user's only copy of valuable data until the remaining release gates are met.

## Current validation baseline

The feature branch `feat/deleted-file-safety-assessment` is preserved; latest implementation and CI changes are on the branch. Pull request #9 was closed without merging; closing the PR did not delete the branch.

The latest Windows CI run [42577224280](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42577224280), for implementation commit `53aa214cc0757ea5aadea57b4acfce89166d32cb`, passed all workflow steps. The roadmap was subsequently reconciled in commit `d0a5d51`. The test suite reported 307 passed, 0 failed, and 1 environment-dependent test skipped (308 total). Release builds reported 0 warnings and 0 errors. CI generated disposable FAT32/exFAT fixtures and successfully smoke-tested `discover-fat` against both, including lost-candidate offset, backup evidence, confidence, and read-only/no-write safety assertions. The same run passed deleted-NTFS extraction/manifest smoke tests, the lost-NTFS recovery harness, WPF build, Windows x64 desktop and CLI publishing, and actual WPF screenshot capture. Earlier WPF integration and cancellation fixes also passed in [run 42575869198](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42575869198).

The screenshot artifact is a genuine Windows WPF dashboard capture in its initial empty state. It confirms the dashboard renders, including the read-only indicator, image selection, inspection/discovery actions, partition overview, and findings panel. It is not a populated partition map or evidence of manual interaction. Manual Windows desktop interaction against generated disposable MBR/GPT/NTFS/FAT32/exFAT images remains outstanding. The deleted-NTFS positive CLI publication path was skipped on this CI runner because its virtualized/layered storage did not expose the unique physical-disk identity metadata required by the production fail-closed gate; unit tests cover rejection and publication logic, and a supported runner is still needed for that positive path. The VHDX environment-dependent test was also skipped. All physical-hardware validation gates remain explicit; no physical HDD recovery is validated.
