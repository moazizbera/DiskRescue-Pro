# DiskRescue Pro — HDD readiness and NTFS safety checklist (2026-11-10)

## Verified mainline milestones

- [x] PR #19 — reject sparse runs in the initial unnamed MFT DATA runlist.
  - Merge commit: 71a2159f6c1128521892c074b1d858d1d9e7b6bc
  - Exact-head Windows CI: [run 42784694182](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42784694182) — successful.
- [x] PR #20 — fail closed when a resident ATTRIBUTE_LIST references a continuation FILE record outside the currently validated initial MFT map.
  - Merge commit: 0fad83a739060f068c28ed13c8c85fe879e7c96f
  - Exact-head Windows CI: [run 42791065483](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42791065483) — successful.
- [x] Post-merge mainline CI after PR #21: [run 42794218012](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42794218012) — all 22 job steps successful.
- [x] PR #22 — final full pre-read revalidation after opening the read-only source handle and before the first source read; fail closed if inventory, source identity, or destination separation changes. Regression tests assert zero reads, handle disposal, and no output files on late inventory/destination failure.
  - Merge commit: `10c8a15e136423806bf96864958c06697243af0f`.
  - Exact-head Windows CI: [run 42800521567](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42800521567) — successful.
  - Post-merge mainline Windows CI: [run 42819949095](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42819949095) — all 22 job steps successful, including build, tests, generated-image fixtures, CLI and NTFS recovery smoke tests, WPF build, Windows x64 desktop/CLI publishes, and dashboard screenshot capture/uploads.
- [x] Windows x64 desktop preview artifact produced by CI.
- [ ] Issue #23 — pin and revalidate acquisition destination filesystem identity. Code-path review confirmed that image and manifest creation, verification, and publication remain path-based after the final pre-read check. A handle-bound destination session is required; no partial path-only mitigation is accepted. See [Issue #23](https://github.com/moazizbera/DiskRescue-Pro/issues/23).
- [x] Actual WPF dashboard screenshot captured and uploaded. Capture success is automated evidence; human visual review and manual Windows interaction remain separate acceptance activities.

## NTFS MFT continuation boundary

Implemented safeguards:
- Resident ATTRIBUTE_LIST parser with strict UTF-16 and entry-boundary validation, bounded to 16 MiB and 65,536 entries.
- Attribute-list matcher validates referenced record identity, sequence, base reference, attribute type/name/ID and LowestVCN.
- MFT extent-map assembler validates continuous VCN coverage, non-sparse runs, bounds, non-overlapping physical runs and aggregate mapping-pairs limits.
- Initial MFT reader rejects sparse mapping-pairs runs.
- Continuation discovery reads referenced FILE records only when already covered by the validated initial MFT map.

Explicit limitation:
- A continuation FILE record outside the initial validated MFT map cannot currently be bootstrapped. This remains intentionally unsupported and fails closed.
- The accepted design decision is recorded in [ADR — NTFS MFT continuation bootstrap boundary](adr/ADR-NTFS-MFT-CONTINUATION-BOOTSTRAP.md). No generic on-volume bootstrap or blind raw-volume FILE-signature scan is implemented.

## First real-HDD test readiness

**Current status: NOT APPROVED for physical HDD partition scanning or recovery.**

The product is an engineering preview. Partition inspection and lost-partition scanning operate on regular disk-image files. Physical-device partition scanning and recovery remain disabled. The separate experimental physical acquisition path has passed automated pre-read revalidation tests on in-memory fixtures, but has not been validated on physical hardware and is not a substitute for a hardware write blocker or forensic acquisition process.

### Stage 1 — Windows application and disposable media
- [x] Windows x64 WPF and CLI previews published by CI.
- [x] Generated-image CLI smoke tests and deleted-NTFS extraction/manifest smoke tests passed in CI.
- [ ] Download and launch the Windows preview on a real Windows host; record OS/build and application startup result.
- [ ] Human review of the captured dashboard screenshot and visual partition navigator.
- [ ] Keyboard-only navigation and visible focus.
- [ ] Narrator/live-region announcements.
- [ ] 100%, 125%, 150%, and 200% scaling plus constrained work-area checks.
- [ ] Manual generated-image smoke test with before/after fixture SHA-256 comparison.
- [ ] Execute the opt-in disposable Windows VHDX harness on a Windows host; record VHDX identity, test output, and before/after hashes. Do not use a valuable disk.

### Stage 2 — physical-device read-only preflight (not partition scanning)
Do not begin until Stage 1 passes and the physical-access code receives a dedicated security review.
- [ ] Verify the device identity and geometry reported by Windows against an independently recorded device label/model/capacity; do not rely on disk number alone.
- [ ] Verify the source handle requests read access only and every code path rejects write access.
- [ ] Verify boot-critical disk exclusion, complete volume-to-disk extent mapping, supported non-layered bus identity, and unique reported serial requirements.
- [ ] Verify source identity/geometry revalidation and destination physical-disk separation before and during acquisition, including the final pre-read revalidation added in PR #22. **Still blocked by Issue #23:** bind image/manifest create, verify, and publish operations to one pinned destination directory/volume identity. PR #22 narrows, but does not eliminate, Windows mount/reparse and concurrent-storage race windows.
- [ ] Use a hardware write blocker for any irreplaceable or evidentiary source; software read-only access alone is not hardware write protection.
- [ ] Test only with a spare, non-critical HDD and a separate destination disk. Stop on clicking, repeated disconnects, overheating, or persistent I/O errors.
- [ ] Record acquisition length, SHA-256, manifest, and source/destination identity evidence; do not interpret the copied-image hash as proof of original file integrity.

### Stage 3 — physical partition discovery and recovery
- [ ] Separate security design and implementation review required.
- [ ] Physical partition scanning must remain disabled until read-only device access, source identity, bounds, cancellation, and failure behavior are independently validated.
- [ ] Recovery must write only to a separate destination device and must never modify the source.
- [ ] No formatting, initialization, repair, repartitioning, mounting, partition-table writes, or source-media writes.

## Manual acceptance still pending

- [ ] Windows host launch and actual user interaction.
- [ ] Keyboard-only navigation and visible focus.
- [ ] Narrator/live-region announcements.
- [ ] 100%, 125%, 150%, and 200% DPI/scaling and constrained layouts.
- [ ] Manual generated-image smoke tests with before/after SHA-256.
- [ ] Disposable Windows VHDX host validation.
- [ ] Physical-device preflight review and spare-HDD acquisition test.
- [x] Hard-gate the public physical-acquisition API and WPF Acquire action while the pinned destination session is absent; the UI now labels destination checks as preflight-only. Regression test asserts the public entry point fails closed without creating an image or manifest. This is a safety disablement, not implementation of the native session.
- [ ] Resolve Issue #23 with handle-bound destination identity, deterministic Windows reparse/path-replacement tests, and exact-head plus post-merge CI.
- [ ] Positive deleted-file export on supported Windows storage with complete physical identity metadata.

## Safety invariants

- Automated CI uses generated images and disposable fixtures; it does not validate physical HDD recovery.
- Physical-device partition scanning and recovery remain disabled.
- The experimental physical acquisition workflow is a guarded read-only image-copy foundation, not a validated hardware recovery feature.
- Physical acquisition is currently hard-disabled at the public API and desktop UI until a verified handle-pinned Windows destination session and deterministic Windows race tests are implemented; a successful path-based preflight is not authorization to copy.
- A read-only raw handle does not prevent Windows or another process from writing through mounted filesystems. Hardware write blocking is strongly recommended for irreplaceable/evidentiary media.
- The $Bitmap allocation state is not proof that old file bytes remain intact or were never overwritten.
- A successful export hash identifies exported bytes only; without a known original hash it is not an original-integrity verdict.
- Never test against the only copy of important data. Use generated images, disposable VHDX, or a spare non-critical HDD with separate destination storage.
