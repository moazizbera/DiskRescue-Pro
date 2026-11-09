# DiskRescue Pro — Implementation and Release Checklist

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
