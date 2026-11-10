# Current status update — 2026-11-10 (desktop accessibility and operation feedback)

### Implemented on `feat/desktop-accessibility-focus-states`

- Added a consistent high-contrast keyboard focus visual to buttons, combo boxes, text boxes, data grids, tabs, and the image navigator tree.
- Added accessible names and task-specific tooltips to primary image actions, navigator/inventory/candidate grids, NTFS search/results, export controls, recovery queue actions, and experimental acquisition entry point.
- Marked changing application, scan, and export status text as polite live regions so assistive technology can announce operation progress without interrupting the user.
- Added explicit UI Automation `LiveRegionChanged` event notifications when application, scan, and recovery queue status text changes; `AutomationProperties.LiveSetting=Polite` alone declares intent but does not guarantee announcements.
- Kept safety wording explicit: candidate evidence is not restoration; deleted-file export remains gated by separate physical destination checks; experimental physical acquisition is not physical partition recovery.
- Initial PR head `d15ea625eeffac5621845eab064c2cd9a7c758a5` passed full Windows CI run [42777645235](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42777645235), including 307 tests, generated-image recovery smoke tests, WPF build/publish, screenshot capture, and artifact uploads.
- Follow-up commit `1f37446dfac34119b0714f14e668992525fb2fef` adds explicit live-region event notifications; exact-head CI is running. Do not merge until this exact head passes and its screenshot is reviewed.
- Manual keyboard-only, screen-reader, high-DPI, and Windows interaction testing remains outstanding.

### Acceptance still pending

- [ ] Confirm focus indicators are visible against all dark surfaces and do not obscure control content.
- [ ] Verify tab, tree, grid, button, and cancellation workflows using keyboard only.
- [ ] Verify live announcements with Windows Narrator or another screen reader.
- [ ] Verify 100%, 125%, 150%, and 200% display scaling and common work-area sizes.
- [ ] Complete the manual Windows smoke-test procedure and compare source fixture SHA-256 values before/after.

### Safety boundary

All automated tests must continue using generated images and disposable VHDX only. Physical-device partition scanning/recovery remains disabled. Manual physical acquisition remains experimental and is not part of this accessibility change.

---

# Current status update — 2026-11-10 (PR #17 merged: responsive inspector)

### Mainline result

- [x] Merged [PR #17](https://github.com/moazizbera/DiskRescue-Pro/pull/17) using squash merge. Merge commit: `12ef98db88811442df86a9844414da44f5d40e10`.
- [x] Exact PR head `317f0de01cff64ae61a60c855a49d26135a5419c` passed Windows CI [run 42775484075](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42775484075): solution restore/build, 307 tests, generated-image CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and artifact uploads.
- [x] Downloaded and visually reviewed the screenshot artifact from that exact run. The inspector is wider and the details TextBox has an explicit vertical scrollbar; the visible viewport remains compact so metadata can be reached without expanding over the findings workspace.
- [x] PR #17 preserves the existing screen-aware window bounds and short-work-area map-collapse behavior.
- [ ] Manual Windows resize/interaction testing remains outstanding. The CI screenshot is a captured preview, not proof of interactive behavior at multiple display resolutions.
- [ ] Compare generated fixture SHA-256 values before/after manual interaction tests.
- [ ] Physical-device partition scanning/recovery remains disabled; no physical HDD recovery is validated or claimed.

### Current desktop workspace acceptance

- [x] Image navigator, validated MBR/GPT partition nodes, synchronized partition grid and selected-item inspector.
- [x] Proportional partition map and selected-partition highlight.
- [x] Inspector geometry: table, index, start LBA, byte offset, exclusive end, byte length, image identity and read-only interpretation.
- [x] Responsive inspector width and explicit internal vertical scrolling.
- [ ] Verify common laptop resolutions, constrained work areas, minimum dimensions, and no clipped controls through manual Windows testing.
- [ ] Verify empty/loading/scanning/cancelled/warning/failed states.
- [ ] Keyboard navigation, visible focus, accessibility names/tooltips, and high-DPI checks.
- [ ] Capture visual baselines at 1366×768, 1920×1080, and a constrained work area.

### Safety boundary

All automated validation uses generated disk images. Source images remain read-only. No formatting, initialization, repair, repartitioning, mounting, partition-table writes, or source-media writes. Recovery output must remain on separately verified physical storage. A software read-only handle is not a hardware write blocker. Passing CI and screenshot review do not establish physical HDD recovery capability.

---

# Current status update — 2026-11-10 (responsive inspector follow-up)

### Implemented on `feat/inspector-responsive-layout`

- Widened the selected-item inspector proportionally and reduced the navigator workspace share so long metadata values have more room.
- Reduced inspector card padding slightly to preserve usable content width.
- Tuned the partition-details viewport for compact work areas and made its internal vertical scrollbar explicit so the final metadata lines remain reachable without consuming the findings panel.
- Kept the existing screen-aware window bounds and short-work-area map collapse behavior.
- Validation is pending: do not claim build, automated tests, screenshot, or Windows manual resize checks passed until CI and visual review complete.
- Safety boundary unchanged: regular image files only for partition inspection; physical-device partition scanning/recovery remains disabled.

# Current status update — 2026-11-10 (professional workspace implementation in progress)

### Implemented in `feat/pro-workspace-navigator-inspector`

- Added an image navigator tab with a root node for the opened regular image and child nodes for detected MBR/GPT partitions.
- Added a selected-partition inspector showing table, name, index, start LBA, byte offset, exclusive end, byte length, image identity and read-only interpretation.
- Synchronized navigator and partition-grid selection; selecting a navigator node scrolls the matching inventory row into view.
- Added a visible accent outline to the selected partition in the proportional map while retaining unallocated gaps and lost-candidate overlays.
- Expanded the map lane for clearer reading. No partition mutation or live physical-device partition browsing was added.
- Validation is pending for this feature branch: do not mark Windows build, automated tests, or visual behavior as passed until CI and Windows review complete.

# Current status update — 2026-11-10 (DiskGenius UX benchmark and professional workspace plan)

This section records the new UX benchmark supplied by the project owner. The DiskGenius screenshots are reference material for information architecture only; DiskRescue Pro must retain its read-only, image-first recovery safety model.

## UX benchmark translated into DiskRescue Pro

| DiskGenius reference | DiskRescue Pro direction | Safety boundary |
|---|---|---|
| Physical-disk / partition tree | Add a dedicated image-and-partition navigator with clear hierarchy and selected-item state | Show only the currently opened regular image; no live-device partition browsing |
| Proportional partition map | Keep the visual map synchronized with the partition navigator and details | Map is evidence visualization, never an editable partition surface |
| Partition table with filesystem, start/end and capacity | Provide a readable, sortable partition inventory with sector and byte boundaries | Metadata is read-only; no partition mutation |
| Detailed volume information | Add a focused details inspector for geometry, identifiers, filesystem and validation evidence | Distinguish observed values from inferred or unavailable values |
| Context-sensitive menus | Offer safe actions appropriate to the selected image, partition, candidate or file entry | No format, delete, resize, extend, split, repair, initialize, mount, or partition-table write commands |
| File operations and recovery | Make preview, assessment, destination preflight and queued export discoverable | Export only through existing bounded, fail-closed recovery workflow |

## Professional workspace implementation checklist

- [x] P0: Add an image/partition navigator; selecting a node synchronizes the partition grid and details view. (Feature branch implementation; CI pending.)
- [x] P0: Add a selected-partition inspector for currently available table/name/index/start-LBA/byte-offset/end/length/image details. Filesystem geometry, identifiers and validation evidence remain follow-up enrichment.
- [x] P0: Highlight the selected partition in the proportional map; exact start/end/length values are exposed in the inspector and map tooltip. Preserve unallocated gaps.
- [ ] P0: Keep the workspace usable at common laptop resolutions and Windows work-area sizes; verify resize behavior, minimum dimensions, scrollability and no clipped controls.
- [ ] P0: Make empty, loading, scanning, cancelled, warning and failed states visually distinct and understandable.
- [ ] P1: Add safe context actions by selection type (inspect image, preview filesystem metadata, review candidate evidence, inspect file entry, queue eligible deleted-file export).
- [ ] P1: Improve partition inventory columns and sorting for filesystem, status, start sector, end sector, capacity and evidence source.
- [ ] P1: Improve queue visibility with per-file progress/status, destination summary, cancellation and actionable failure reasons.
- [ ] P1: Add keyboard navigation, visible focus states, accessible names/tooltips and high-DPI checks.
- [ ] P2: Capture and review fresh Windows screenshots at 1366×768, 1920×1080 and a constrained work area; attach artifacts to CI.
- [ ] P2: Add a documented visual regression baseline and release-readiness review.

## UX acceptance and safety criteria

- [ ] Navigator, map, grid and details always refer to the same selected image/partition; stale selection/details are cleared when the image changes. (Implemented selection path; verify with CI and Windows manual smoke.)
- [ ] Selecting a lost-partition candidate never implies that a partition was restored or that the partition table was changed.
- [ ] Every recovery action remains gated by current metadata validation, byte limits, cancellation, no-overwrite publication and verified separate physical destination.
- [ ] No destructive DiskGenius menu operation is introduced, even as a disabled or misleadingly available command.
- [ ] UI screenshots are generated from synthetic disposable images; never from the user's original HDD.
- [ ] Manual Windows visual and hardware checks are recorded as manual, not inferred from a successful CI build.

## Reference interpretation

The supplied DiskGenius V5.3 screenshots demonstrate a compact hierarchy: disk navigation, proportional partition layout, a detailed partition list, a metadata pane, and selection-sensitive menus. We will adapt that hierarchy to an image-file workspace. The product should feel like a professional disk-analysis console while remaining intentionally narrower and safer than a partition manager.

---

# DiskRescue Pro — Implementation and Release Checklist

# Current status update — 2026-11-10 (mainline integration merged)

This is the authoritative mainline snapshot. Historical branch notes below are retained for traceability and may describe earlier states.

## Mainline merges verified

- PR #3 — bounded in-memory NTFS MFT extent-map validation; merge commit `87a20c65de76256a5a1c28e116da344223fdb4ca`.
- PR #4 — read-only NTFS lost-partition discovery for regular image files; merge commit `4c3f260b1e943fc487a99f395193a5a5e62969ef`.
- PR #12 — guarded read-only physical-disk imaging foundation; merge commit `3912d6b3bdc9af4bc093a055f1d0b301842b2e36`.
- PR #13 — opt-in disposable-VHDX safety smoke harness; merge commit `b73e7c8ae922f374d49c7f0c89c7d1272f201bb8`.
- PR #14 — desktop redesign, visual partition map, FAT32/exFAT discovery, bounded deleted-NTFS export, and multi-file recovery queue; merge commit `5811494bf8e9b10f90b08ef810bc79854a1a0346`.

## Current mainline validation

- Mainline code merge commit: `5811494bf8e9b10f90b08ef810bc79854a1a0346`.
- PR #14 exact head `5f34907da2683f9449ff27797b81dbe11f2efd7e`: Windows CI [#42765242075](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42765242075) passed.
- Post-merge mainline CI [#42765478069](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42765478069) passed.
- All pull requests in the integration chain are closed; no open PRs remain at this snapshot.

## Current checklist

- [x] Read-only regular image inspection, MBR/GPT validation, and backup-GPT checks.
- [x] Bounded NTFS lost-partition discovery with backup-boot-sector corroboration and generated fixtures.
- [x] Bounded in-memory MFT extent-map validation; continuation-record acquisition and full reader integration remain separate work.
- [x] FAT32/exFAT discovery and backup-region validation.
- [x] Candidate overlap classification, evidence/confidence reporting, visual partition map, and candidate comparison.
- [x] Physical-disk discovery and guarded read-only image acquisition with fail-closed source/destination preflight.
- [x] Disposable-VHDX policy smoke coverage and opt-in Windows harness.
- [x] Deleted-NTFS assessment and bounded export safeguards with manifest/hash publication.
- [x] Multi-file deleted-file queue with per-file status, sequential progress, cancellation, and safe failure isolation.
- [ ] P0: Manual Windows desktop smoke test using generated disposable images: partition map, candidate comparison, NTFS file search, queue, cancellation, and JSON report.
- [ ] P0: Positive deleted-file export on supported Windows storage exposing complete physical identity metadata. Hosted CI intentionally fails closed where that metadata is unavailable.
- [ ] P1: Run the opt-in VHDX harness on a disposable Windows VHDX host; not claimed as executed.
- [ ] P1: Inspect the latest WPF screenshot and verify resizing, work-area bounds, queue visibility, and readable controls.
- [ ] P2: Signed installer, versioned release workflow, and broader Windows-version/usability testing.

## Safety gates

- Never test against an original, damaged, or irreplaceable HDD. Use generated images and disposable VHDX only.
- Source media and source images remain read-only. No formatting, initialization, repair, repartitioning, mounting, or partition-table writes.
- Physical-disk acquisition is an image-copy operation, not validated physical-device partition recovery.
- A software read-only handle is not a hardware write blocker; use a hardware write blocker for evidentiary/irreplaceable media.
- Recovered output must be on a separately verified physical destination.
- Exported SHA-256 identifies the exported bytes; it does not prove original file integrity without a known original hash.
- A green CI run validates the exact tested commit only. Manual hardware and desktop checks must be recorded separately.

# Current status update — 2026-11-09 (file-entry search CI verified)

This snapshot supersedes conflicting historical status notes below.

- Active branch: `feat/deleted-file-safety-assessment`
- Current branch head at this update: `f3a580dfca0f96b4ee4efce87eef61c269a89fdc`.
- File-entry search/reset implementation: `bde473821a8517eb59fa6c4ab105dc1f93e421e6`; Windows CI [run 42583133945](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42583133945) — **passed**.
- Checklist documentation commit: `f3a580dfca0f96b4ee4efce87eef61c269a89fdc`; Windows CI [run 42583146423](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42583146423) — **passed**.
- Candidate comparison UI: `65ba6afdd8fa6d1d19a1fd3357e979b775ed19de`; Windows CI [run 42581920342](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42581920342) — **passed**.
- Advisory candidate-review guidance commits `c5c3b15152e1b883ac4577cc8413241ae40ca8cd` and `21b758178e005497606b3ac2f064a15838d8ba57` — previously verified CI passed.
- File-entry tab supports filename, MFT record, parent reference, attributes and type filters; sortable metadata columns; selected-entry details; and clearing stale state when the source image changes.
- Latest verified CI job on the file-entry implementation completed successfully. This run includes Release build/tests, generated-image fixtures, CLI smoke/recovery harnesses, WPF build/publish and artifact stages.
- Recovery destination foundation already includes fail-closed physical-volume separation checks, free-space/filesystem capacity checks, physical disk identity checks, and no-overwrite paired output/manifest publication. The desktop destination/extraction workflow and positive export on supported physical storage remain unverified.

## Current checklist

- [x] Read-only image inspection, MBR/GPT validation, and backup-GPT checks.
- [x] NTFS lost-partition discovery and bounded metadata preview.
- [x] FAT32/exFAT discovery and backup-region validation.
- [x] Candidate overlap classification and evidence-based confidence.
- [x] WPF discovery list, partition-map overlays, and findings integration.
- [x] Populated synthetic WPF discovery screenshot using real Inspect/Discover handlers.
- [x] Candidate comparison UI and selected-candidate evidence detail; Windows CI passed.
- [x] Deleted-NTFS safety assessment, bounded export, manifest/hash and publication safeguards (automated coverage).
- [x] Cancellation and temporary-file safeguards (automated coverage).
- [x] Conservative candidate review guidance; Windows CI passed; no automatic partition-table changes.
- [x] NTFS MFT file-entry search, filter, sortable columns, selected-entry metadata details, and reset-state handling; Windows CI passed.
- [ ] P0: Manual Windows desktop smoke test with disposable images, including candidate comparison, file-entry search, and cancellation.
- [ ] P0: Positive deleted-file export CLI publication on supported storage. CI correctly fails closed and skips this positive path when virtualized storage does not expose the physical identity metadata required by the production gate; unit tests cover safeguards.
- [ ] P1: Desktop recovery destination verification and extraction queue, including accessible progress/cancellation/failure states.
- [ ] P2: Signed installer, versioned release workflow, and broader manual Windows-version/usability testing.

## Safety gates

- Physical-device partition scanning/recovery remains disabled; physical acquisition is not validated on real hardware.
- No source-media repair, formatting, initialization, repartitioning, or partition-table writes.
- Use generated images and disposable VHDX only; never test against the original HDD.
- Export hashing validates exported bytes only, not original-file integrity without a known original hash.
- Recovery output must be on separate physical storage; use a hardware write blocker for irreplaceable/evidentiary media.


# Current status update — 2026-11-09

This snapshot supersedes conflicting historical status notes below.

- Active branch: `feat/deleted-file-safety-assessment`
- Verified commit: `192945bd66694bdb1e51d4e415949ed16689271c`
- Windows CI: [run 42580801742](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42580801742) — passed on this exact commit.
- Tests: 307 passed, 0 failed, 1 skipped (308 total).
- Release solution build and WPF build: 0 warnings, 0 errors.
- CI generated a disposable FAT32 image, ran the real WPF Inspect and Find lost partitions handlers, and captured a populated screenshot. The screenshot shows one lost FAT32 candidate in the partition list and map. Artifact: `DiskRescue-Pro-Desktop-Screenshot`.
- Windows x64 desktop and CLI previews and test results were uploaded.

## Current checklist

- [x] Read-only image inspection, MBR/GPT validation, and backup-GPT checks.
- [x] NTFS lost-partition discovery and bounded metadata preview.
- [x] FAT32/exFAT discovery and backup-region validation.
- [x] Candidate overlap classification and confidence/evidence reporting.
- [x] WPF discovery list, map overlays, and findings integration.
- [x] Deleted-NTFS safety assessment, bounded export, manifest/hash and publication safeguards (automated coverage).
- [x] Cancellation and temporary-file safeguards.
- [x] Populated synthetic WPF discovery screenshot, captured and visually inspected.
- [ ] P0: Manual Windows desktop smoke test using disposable images.
- [ ] P0: Positive deleted-file export CLI publication on supported storage. CI correctly fails closed and skips this path because its virtualized destination does not expose the physical identity metadata required by the production gate; unit coverage remains green.
- [ ] P1: Candidate comparison and conflict-resolution UI.
- [ ] P1: File-entry search, filtering, sorting, and metadata inspection.
- [ ] P1: Accessible progress/cancellation/failure states and verified recovery destination/extraction queue.
- [ ] P2: Signed installer, versioned release workflow, and broader manual usability/Windows-version testing.

## Safety gates

- Physical-device partition scanning/recovery remains disabled; physical acquisition is not validated on real hardware.
- No source-media repair, formatting, initialization, repartitioning, or partition-table writes.
- Use generated images and disposable VHDX only; never test against the original HDD.
- Export hashing validates exported bytes only, not original-file integrity without a known original hash.
- Recovery output must be on separate physical storage; use a hardware write blocker for irreplaceable/evidentiary media.

---


Status snapshot: 2026-11-06 (Phase 2 continuation-discovery branch). This document distinguishes merged baseline evidence from the current feature branch. A green check on an older SHA is not validation of a later worktree.

The attribute-list foundation was merged into `main` by PR #2 at `6008e2edd6b034c3d7835e69388fbf6fed576fdf`. The new branch `feat/ntfs-mft-continuation-discovery` adds an initial in-memory MFT extent-map assembler and synthetic tests. This feature branch is not yet merged or independently CI-validated.

Status key:

- **Verified complete** — implemented and supported by the evidence stated here.
- **Not implemented** — no implementation is present.
- **[!] Implemented but not independently verified** — code exists, but required evidence for the current candidate is incomplete.
- **[~] Partially implemented** — a limited subset exists; the stated broader capability is not complete.

## A. Project overview

DiskRescue Pro is a Windows-first, safety-focused toolkit for inspecting regular disk-image files. Its current architecture separates bounded read-only IO, MBR/GPT parsing, NTFS metadata parsers, a CLI, and a WPF desktop shell. Physical-device access and source-write APIs are not implemented.

- Repository: `moazizbera/DiskRescue-Pro`
- Development branch: `feat/ntfs-attribute-list-foundation`
- Target branch: `main`
- Pre-preservation branch tip: `6676f202e06c2fc97c35b1152a9b93e04dbf031d`.
- Preservation implementation commit: `91542a93cd6b9838f90f55db0bfd88ab5484aa28` (`feat(ntfs): add bounded attribute continuation matching`); pushed to `origin/feat/ntfs-attribute-list-foundation`, and verified local and remote refs matched.
- `origin/main` at inspection: `5f9b0e8144e5a27ea9d0eeaeeddb5c239315eaa1`.
- Target framework: .NET 10; core, CLI, and tests target `net10.0`; WPF targets `net10.0-windows`.
- Current milestone: bounded resident `$ATTRIBUTE_LIST` parsing and one-level matching against caller-supplied, already validated FILE records. This is not automatic MFT continuation discovery.
- PR #2: https://github.com/moazizbera/DiskRescue-Pro/pull/2 — open, targets `main`, and its remote head is still the older `6676f20` commit at this snapshot.
- Latest known Actions run for the preservation commit: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42207867050 — in progress when checked, on `91542a93cd6b9838f90f55db0bfd88ab5484aa28`. Its PR check was also in progress.
- Earlier passing PR run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42100130497 — passed on the pre-preservation SHA `6676f202e06c2fc97c35b1152a9b93e04dbf031d` only.

The local worktree was clean after the preservation commit and push. This checklist itself was included in commit `91542a93cd6b9838f90f55db0bfd88ab5484aa28`.

## B. Completed implementation checklist

| Capability | Status | Evidence / boundary |
|---|---|---|
| Read-only regular image-file access | Verified complete | `FileImageReader` opens with read access; IO exposes bounded reads and no write API. Unit tests cover bounds and physical-device namespace rejection. |
| MBR parsing | Verified complete | Parser tests and generated MBR CLI fixture. |
| GPT parsing and structure validation | Verified complete | Tests and generated valid/damaged-primary fixtures; primary and backup copies are validated independently. |
| GPT CRC checks | Verified complete | Header and partition-array CRC validation covered by tests and generated-image CLI smoke runs. |
| NTFS boot-sector validation | Verified complete | Geometry and supplied-volume-boundary checks have synthetic tests. |
| FILE record parsing and USA fixups | Verified complete | Synthetic FILE records test validation and fixup behavior. |
| NTFS attribute parsing | Verified complete | Bounded resident/non-resident header parser; strict UTF-16 attribute-name decoding. |
| NTFS mapping-pairs decoding | Verified complete | Synthetic tests cover fragmented and sparse runs and bounds. |
| Initial `$MFT::$DATA` extent reading | [~] Partially implemented | Reads and scans only through the initial validated MFT extent, including records that cross its physical runs. |
| Resident `$FILE_NAME` and parent references | Verified complete | Synthetic parser tests. |
| Bounded MFT scanning | [~] Partially implemented | Scan is capped and limited to initialized data covered by the initial extent. |
| Partial directory-tree construction | [~] Partially implemented | Conservative tree from scanned entries with parent sequence checks, cycle handling, and orphan diagnostics; not a complete tree. |
| `$ATTRIBUTE_LIST` binary parsing | Verified complete | Existing parser tests plus current local test run. Parser handles an already-read resident value; enforces 16 MiB and 65,536-entry limits, aligned entry lengths, bounded name ranges, and strict UTF-16. |
| `$ATTRIBUTE_LIST` attribute integration | [~] Partially implemented | Parser overload accepts a parsed resident attribute and rejects non-resident list streams. No stream reader is implemented. |
| Strict UTF-16 validation | Verified complete | Strict decoders reject malformed/unpaired surrogates in list and attribute names; synthetic tests exercise malformed names. |
| Attribute continuation identity matcher | Verified complete (local only) | In-memory matcher and synthetic-record tests exist. Current local full test run: 99 passed, 0 failed. No remote CI run for the matcher commit yet. |
| Record number / sequence validation | Verified complete (local only) | Matcher checks referenced number and sequence; values not representable by supported FILE record numbers are rejected. |
| Base-file reference validation | Verified complete (local only) | Matcher verifies continuation base reference against expected base record and sequence; base record must have a null base reference. |
| Resource limits and diagnostics | Verified complete (local only) | Details in section C. Rejections are structured and prevent a complete result. Work is bounded; there is no cancellation API because the in-memory operation is capped. |
| CLI | [~] Partially implemented | Read-only image inspection and bounded NTFS scan work. The matcher is not wired into the CLI and does not extend its MFT coverage. |
| WPF desktop | [~] Partially implemented | Image/partition inspection and selected-partition NTFS preview. The matcher is not wired into the UI. |
| Generated-image fixtures | Verified complete | PowerShell tools generated disposable MBR/GPT and synthetic NTFS images in a temporary directory. No physical disk was accessed. |
| Automated tests | Verified complete (local only) | `dotnet test DiskRescue.sln --configuration Release --no-build`: 99 passed, 0 failed, 0 skipped for the local candidate at the time of the run. |
| Current-candidate Windows x64 CLI publish | [!] Implemented but not independently verified | Latest local `dotnet publish` attempt failed during restore with NU1900 because NuGet vulnerability data at `https://api.nuget.org/v3/index.json` was unreachable. |
| Current-candidate Windows x64 WPF publish | [!] Implemented but not independently verified | Same NU1900 restore failure. An earlier local publish succeeded before subsequent edits; it is not evidence for the current candidate. |
| Remote Windows CI for current matcher | [!] Implemented but not independently verified | Latest known successful workflow ran on old PR head `6676f20`; it does not contain current uncommitted matcher changes. |

## C. Current matcher implementation

`NtfsAttributeListParser` parses an already-read resident `$ATTRIBUTE_LIST` value. It returns entry type, entry length, name, lowest VCN, 48-bit reference record number, 16-bit sequence number, and attribute ID. It rejects empty, truncated, malformed, oversized, and non-resident inputs. Names are decoded as strict UTF-16. A zero-length name does not require the on-disk optional-name offset to be zero.

`NtfsAttributeListMatcher` takes the parsed list, a bounded collection of FILE records already supplied by its caller, and the expected base reference. It performs no disk IO. It matches:

1. Referenced MFT record number and sequence number.
2. Continuation record base-file reference against the expected base record and sequence; for the base record itself, sequence must match and base reference must be null.
3. Attribute type.
4. Attribute name using ordinal, case-sensitive string comparison after strict UTF-16 decoding.
5. Attribute ID.
6. Lowest VCN (resident attributes are treated as VCN 0 for this identity check).

It rejects duplicate list identities, missing or duplicate/ambiguous candidate records, stale sequence values, unused records, incorrect base references, malformed candidate attributes, each attribute-identity mismatch, and ambiguous matching attributes. Diagnostics include a stable code, optional list entry index, and message. A result containing any rejection is not marked complete; a partial set of matches is not presented as complete coverage.

Explicit limits:

- Parsed list value: 16 MiB maximum.
- Attribute-list entries: 65,536 maximum.
- Candidate records: 4,096 maximum.
- Aggregate candidate-record bytes: 16 MiB maximum.
- Individual candidate FILE record: 1 MiB maximum.
- Attributes parsed for a match: 4,096 maximum.
- Attribute comparisons: 1,000,000 maximum.

The matcher does not fetch records. Its successful result means only that supplied entries matched supplied records; it does not establish that the attribute list or MFT is complete.

## D. Outstanding engineering work

- Read referenced MFT records outside the initial validated MFT extent, using only a validated bounded mapping.
- Support non-resident `$ATTRIBUTE_LIST` streams.
- Define and implement safe recursive continuation discovery with cycle/depth controls.
- Assemble MFT continuation extents with exact VCN continuity, no gaps/overlaps, and size/volume bounds.
- Wire the matcher and any future continuation reader into CLI and WPF only after completeness semantics are explicit.
- Do not claim complete MFT coverage or complete fragmented-file reconstruction.
- Implement file-content mapping, extraction, and recovery only as separate future work with strict source/destination separation.
- Perform manual Windows desktop interaction testing with generated fixtures.
- Add generated-image integration coverage for continuation records and extent assembly when that feature is implemented.
- Add parser fuzzing and malformed-image regression coverage.
- Complete lost-partition discovery, directory-index traversal, FAT/exFAT support, and remaining roadmap work.
- Resolve the outstanding PR review threads and obtain CI for the exact updated PR commit.
- Publish current candidate artifacts after NuGet connectivity is available (or the cause of NU1900 is otherwise safely resolved without weakening audit policy).

## E. Validation evidence

### Local evidence for the current candidate

| Check | Result | Actual command / details |
|---|---|---|
| Restore | PASS | `dotnet restore DiskRescue.sln` succeeded before local validation. |
| Release solution build | PASS | Final `dotnet build DiskRescue.sln --configuration Release` succeeded after the final test additions; all six solution projects built. |
| Full tests | PASS | `dotnet test DiskRescue.sln --configuration Release --no-build`: 99 passed, 0 failed, 0 skipped. |
| Generated-image CLI smoke tests | PASS | MBR, valid GPT, damaged-primary/valid-backup GPT, NTFS partition geometry, and `scan-ntfs` were run on generated fixtures. The NTFS scan returned `Report.txt` and confirmed read-only mode. These were manual CLI invocations, not a single new CI run. |
| Windows x64 CLI publish | FAIL / BLOCKED | `dotnet publish src/DiskRescue.Cli/DiskRescue.Cli.csproj --configuration Release --runtime win-x64 --self-contained true --output C:\Users\Abeer\AppData\Local\Temp\DiskRescue-PR2-Publish-CLI` failed restore with NU1900 because NuGet vulnerability metadata was unreachable. |
| Windows x64 WPF publish | FAIL / BLOCKED | `dotnet publish src/DiskRescue.Desktop/DiskRescue.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output C:\Users\Abeer\AppData\Local\Temp\DiskRescue-PR2-Publish-Desktop` failed restore with the same NU1900 error. |
| `git diff --check` | PASS | Passed after the checklist was added and again on the staged changes before commit. |

The failed publish commands produced no confirmed artifacts at their output paths. Do not equate older successful local publishes with the current candidate.

### Remote CI evidence

- Latest known PR CI: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42100130497 — PASS, but only for `6676f202e06c2fc97c35b1152a9b93e04dbf031d`.
- It ran restore, Release build/tests, generated-image smoke tests, WPF build, Windows x64 publishes, and artifact upload on that earlier commit. The current matcher/test changes were not part of that SHA.
- A new GitHub Actions run against the current commit is required after push.

## F. Safety and release checklist

- [x] Never test against physical disks or volumes; none were accessed for this work.
- [x] Use generated, disposable fixtures only.
- [x] Preserve read-only image access; do not introduce a source-write API.
- [x] Do not write to, repair, initialize, format, repartition, or modify any source image.
- [x] Do not use CHKDSK repair or equivalent operations.
- [x] No destructive disk operation is implemented or authorized.
- [ ] Any future recovered file must go to a separately verified destination, never the source.
- [ ] Require tests, bounds, diagnostics, and explicit completeness semantics before expanding continuation support.
- [ ] Require current-commit GitHub CI and release artifact upload before merge.

## G. Next-session handover

1. Read this checklist, then `README.md`, `docs/SAFETY-POLICY.md`, `docs/ARCHITECTURE.md`, and `docs/ROADMAP.md`.
2. Inspect branch `feat/ntfs-attribute-list-foundation`, its worktree, latest commit, and PR #2 before modifying anything.
3. Review `NtfsAttributeListParser`, `NtfsAttributeListMatcher`, `NtfsAttributeParser`, `NtfsMftReader`, and their tests. Preserve the existing local work; the matcher is not yet committed in the snapshot described here.
4. Run restore, Release build, full tests, and generated-image CLI smoke tests on the exact candidate; repeat `git diff --check`.
5. Resolve NuGet connectivity/audit-data restore failures without disabling security audit or relaxing warnings-as-errors.
6. Run and verify self-contained Windows x64 CLI and WPF publishes.
7. Commit and push intended changes only to `feat/ntfs-attribute-list-foundation`; verify the remote SHA.
8. Check PR #2 and Actions against that exact SHA. Resolve review threads and confirm all required checks/artifacts pass.
9. Continue referenced-record IO, non-resident lists, recursive discovery, VCN-continuous MFT extent assembly, and CLI/WPF integration as separately tested milestones.
10. Merge PR #2 into `main` only after all release gates pass. Do not delete the development branch automatically.

Current reference values at the preservation commit:

- Local and previously fetched feature tip: `6676f202e06c2fc97c35b1152a9b93e04dbf031d`.
- Pushed preservation commit / PR head at snapshot: `91542a93cd6b9838f90f55db0bfd88ab5484aa28`.
- Previously fetched `main`: `5f9b0e8144e5a27ea9d0eeaeeddb5c239315eaa1`.
- PR #2: https://github.com/moazizbera/DiskRescue-Pro/pull/2 (open; remote head is still the older SHA).
- Latest workflow: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42207867050 (in progress on `91542a93cd6b9838f90f55db0bfd88ab5484aa28` when checked).

## UX review additions — 2026-11-09 (Google AI / DiskGenius comparison)

These items are additive to the existing implementation checklist. They do not authorize destructive partition-management features or physical-device recovery.

### Accepted improvements

- [x] Move **Find lost partitions** beside **Inspect image** in the source-action row; remove the unnecessary spacer between dependent actions.
- [x] Elevate the read-only / physical-operation limitation into a visible warning banner inside **03 / INSPECTION REPORT**.
- [x] Preserve the existing partition overview selection workflow and the existing NTFS file-entry / directory-tree preview. These are metadata inspection features, not full filesystem browsing or file-content recovery.
- [ ] Add explicit visual selection/highlighting between a selected partition row and its corresponding partition-map segment; ensure ambiguous/overlapping ranges remain distinguishable.
- [ ] Add evidence-backed filesystem drill-down only where directory-index / filesystem metadata has actually been parsed. Use explicit lazy-load states; do not add placeholder null tree nodes or fabricate file entries.
- [ ] Extend JSON inspection reports with reproducibility/provenance fields where available: application version, UTC timestamp, source identity and size, sector size, scan configuration, source hash (only if actually computed), structured findings, and explicit limitations. Never fabricate confidence, timestamps, hashes, or validation outcomes.
- [ ] Review the JSON report schema for stable versioning and ensure every field accurately describes observed evidence. Exported-file hashes must not be represented as proof of original-source integrity.

### Explicitly out of scope

- [x] Reject partition splitting, alignment changes, geometry tuning, partition-table writes, formatting, initialization, and repair. DiskRescue Pro remains a read-only diagnostic and safe-recovery product; it is not intended to clone DiskGenius' destructive partition-management surface.
- [x] Keep physical-disk partition scanning/recovery disabled until independent device-safety validation is complete. A software read-only handle alone is not a hardware write blocker.

### Validation gate for this UX change

- [ ] Windows CI passes on the exact updated branch commit, including WPF build, full tests, synthetic fixture smoke tests, and screenshot capture.
- [ ] Inspect the newly captured screenshot and confirm the safety banner is legible and the source actions are grouped.
- [ ] Manual Windows smoke test with the seven existing synthetic images; verify partition selection, lost-candidate evidence, NTFS file-entry search, tree preview, cancellation, and JSON export.
- [ ] Compare all seven fixture SHA-256 hashes before and after the manual run.
- [ ] No physical HDD testing is authorized by completion of these UX items.


## CI diagnosis — 2026-11-09 (latest UX commit)

- Commit `8b068d92bec0221952e476245fc74743992dfd93` triggered [workflow run #42671717046](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42671717046), which concluded `failure`.
- GitHub's run-jobs endpoint returned zero jobs; the commit check-runs endpoint also returned zero check runs. No build/test step or diagnostic log was exposed, so this is not evidence of a XAML compilation or test failure.
- The workflow file blob is identical on this commit and the previously passing commit `dceeacb75a2f5ee7ef908de79b8654ace4831cb7`.
- GitHub rejected the API request to re-run this run (HTTP 403: workflow run cannot be retried).
- A fresh CI run is required before marking the UX validation gate complete. Keep PR #10 in draft; do not merge until the exact latest commit passes.


## Current workspace follow-up — 2026-11-10 (PR #16)

This section supersedes older branch/PR snapshots above for the current desktop workspace effort. Historical notes are retained as historical evidence, not as the current project state.

### Current candidate

- Pull request: https://github.com/moazizbera/DiskRescue-Pro/pull/16
- Branch: `feat/pro-workspace-navigator-inspector`
- Workspace feature baseline: `5c5c9f07a4bc4659a859b08e6e38f5bfce645875`
- Follow-up commits: `36415fba75873999cc46b8617ebdac982e2d6fd1` (named map panel), `0615d3b453329ab08285927e36f57f610c30bea4` (compact-height behavior and initial partition focus), and `ef1b8b351e53febf0045c515a83b67597b9f0a74` (CI screenshot fixture).
- PR #16 remains open; do not treat an earlier green run as validation of these later commits.

### Implemented workspace behavior

- [x] Add an image navigator with the image root and validated MBR/GPT partition nodes.
- [x] Synchronize navigator and partition-grid selection; selecting a partition updates the inspector and map highlight.
- [x] Show selected-partition geometry, including table, index, start LBA, byte offset, exclusive end, length, image name, and read-only interpretation.
- [x] Select the first validated partition after image inspection so the initial workspace has a useful focus. If no validated partition-table entry exists, retain the image root; never promote scan candidates into validated partitions.
- [x] On short work areas (under 760 logical pixels high), collapse the partition-map panel to preserve usable space for the navigator and inspector. The map remains visible on normal-height work areas.
- [x] Change the CI screenshot fixture from a lost FAT32 candidate image to a generated valid MBR image, so the screenshot demonstrates a populated partition navigator and inspector. Lost-candidate discovery remains covered by its dedicated generated-image smoke tests.

### Validation evidence and remaining gates

- [x] Baseline commit `5c5c9f07a4bc4659a859b08e6e38f5bfce645875`: Windows CI run [42769291354](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42769291354) passed build, tests, generated-image CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, and screenshot capture.
- [x] Baseline screenshot artifact was downloaded and reviewed. It exposed a cramped workspace and did not demonstrate a populated navigator/selected partition; this prompted the follow-up changes above.
- [ ] Run full Windows CI on the exact latest PR head after the follow-up commits, including screenshot capture and artifact upload.
- [ ] Review the new screenshot artifact at the exact latest PR head for readable navigator, selected partition, inspector details, and compact-height layout.
- [ ] Perform manual Windows interaction tests with generated fixtures: navigator/grid synchronization, partition-map highlight, lost-candidate evidence, NTFS preview, file-entry search, cancellation, and JSON export.
- [ ] Compare generated fixture SHA-256 values before and after manual interaction tests.
- [ ] Obtain user-confirmed manual Windows results. Hosted CI is not a substitute for interactive hardware/display validation.
- [ ] Keep physical-device partition scanning/recovery disabled. No physical HDD test is authorized or claimed.

### Safety boundary

All current validation uses generated disk images. Source images remain read-only. No format, initialize, repair, repartition, partition-table write, or source-media write operation is authorized. Recovery output must remain separate from source storage. A passing build or screenshot does not prove physical HDD recovery capability.


### Screenshot workflow correction — 2026-11-10

- [x] Diagnosed CI run [42769752826](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42769752826): 307 tests passed, solution/WPF builds passed, and Windows x64 desktop publish passed; screenshot capture failed because the CI supplied a valid MBR image to a routine that still asserted for a lost-FAT32 discovery candidate.
- [x] Replaced the mismatched discovery-specific screenshot routine with a workspace screenshot scenario. It runs the real read-only Inspect UI flow against the generated valid MBR fixture, requires a validated MBR/GPT partition, selects it, and refreshes the inspector before capture.
- [x] Renamed the screenshot environment variable to `DISKRESCUE_WORKSPACE_SCREENSHOT_IMAGE` consistently in the WPF startup and CI workflow.
- [ ] Re-run full CI on the exact latest PR head and inspect the resulting screenshot artifact.
- [ ] Merge PR #16 only after all required checks pass. Manual Windows interaction validation remains a separate outstanding gate.


### Screenshot environment-variable correction — 2026-11-10

- [x] Inspected failed run [42770012171](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42770012171) on head `34a4ae1a85326dc25bc58a53844b04aff1767db5`: 307 tests, solution build, generated-image CLI/recovery smoke tests, WPF build, and Windows x64 desktop publish passed.
- [x] Identified the screenshot failure as a stale workflow variable reference: the step set `DISKRESCUE_WORKSPACE_SCREENSHOT_IMAGE` but tested and cleared the former `DISKRESCUE_DISCOVERY_SCREENSHOT_IMAGE`, causing a null-path error before the app launched.
- [x] Corrected both stale references in `.github/workflows/ci.yml` to use `DISKRESCUE_WORKSPACE_SCREENSHOT_IMAGE` consistently. Workflow fix commit: `be8546659af31d31e59551501cf183e24dd6ca02`.
- [ ] Await a fresh full CI run on the corrected head; review its actual screenshot artifact before merge.
- [ ] Keep manual Windows interaction tests and generated-image SHA-256 before/after comparison outstanding. No physical HDD validation is claimed.


### PR #16 merged — 2026-11-10

- [x] Merged [PR #16](https://github.com/moazizbera/DiskRescue-Pro/pull/16) into `main` using squash merge. Merge commit: `7805fb27b16cd92d9858c66691b9924e98f5edd7`.
- [x] Exact merged head `a07e0a7fc22479500fa4179d01ad1076deb17f6f` passed Windows CI run [42770653423](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42770653423): solution build, 307 tests, generated-image CLI/discovery/recovery smoke tests, WPF build, Windows x64 desktop and CLI publishing, WPF screenshot capture, and artifact uploads all succeeded.
- [x] Downloaded and visually reviewed the screenshot artifact from that exact run. It shows the valid MBR image, populated navigator, selected partition, and inspector geometry. The inspector uses a scrollbar and lower interpretation text is not fully visible in the captured viewport; responsive/manual visual review remains worthwhile.
- [ ] Perform manual Windows interaction tests using generated fixtures: navigator/grid synchronization, partition-map highlight, lost-candidate evidence, NTFS preview, file-entry search, cancellation, and JSON export.
- [ ] Compare generated fixture SHA-256 values before and after manual interaction tests and obtain user-confirmed results.
- [ ] Keep physical-device partition scanning/recovery disabled. No physical HDD test is authorized or claimed.

### Safety boundary reaffirmed

All automated validation used generated disk images. Source images remain read-only. No format, initialize, repair, repartition, partition-table write, or source-media write operation is authorized. Recovery output must remain separate from source storage. CI success and the screenshot do not prove physical HDD recovery capability.
