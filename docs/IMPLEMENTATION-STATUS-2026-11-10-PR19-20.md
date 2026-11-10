# DiskRescue Pro — NTFS MFT safety milestones (2026-11-10)

## Merged changes

- [x] PR #19 — reject sparse runs in the initial unnamed $MFT::$DATA runlist.
  - Merge commit: `71a2159f6c1128521892c074b1d858d1d9e7b6bc`
  - Exact PR head `919df353e7d7db3f29db7b40ec8f78355d090d43)
  - Exact-head Windows CI: [run 42784694182](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42784694182) — successful.
  - Regression test asserts an initial sparse MFT runlist is rejected and no reader is returned.
- [x] PR #20 — assert fail-closed behavior when a resident $ATTRIBUTE_LIST references a continuation FILE record outside the currently validated initial MFT map.
  - Merge commit: `0fad83a739060f068c28ed13c8c85fe879e7c96f`
  - Exact PR head `03f318ae3fb2d3eaf6e79774316c1fadbba9203e`
  - Exact-head Windows CI: [run 42791065483](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42791065483) — successful.
  - Test asserts extension is invalid, not extended, returns no reader, and reports the validated-map boundary.
- [x] Mainline CI after PR #19: [run 42790971486](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42790971486) — successful.
- [ ] Post-merge mainline CI after PR #20: pending final result.

## NTFS MFT continuation status

Implemented safeguards:
- Resident $ATTRIBUTE_LIST parser with strict UTF-16 and entry-boundary validation, bounded to 16 MiB and 65,536 entries.
- Attribute-list matcher validates referenced record identity, sequence, base reference, attribute type/name/ID and LowestVCN.
- MFT extent-map assembler validates continuous VCN coverage, non-sparse runs, bounds, non-overlapping physical runs and aggregate mapping-pairs limits.
- Initial MFT reader rejects sparse mapping-pairs runs.
- Continuation discovery currently reads referenced FILE records only when they are already covered by the validated initial MFT map.

Explicit limitation:
- A continuation FILE record outside the initial validated MFT map cannot currently be bootstrapped. This remains intentionally unsupported and fails closed.

## Next design gate — continuation bootstrap

Do not implement speculative physical-address guessing. Before enabling bootstrap, produce a design and synthetic tests for a bounded bootstrap strategy that:
1. Starts only from a fully validated base FILE record 0 and its resident $ATTRIBUTE_LIST.
2. Derives candidate locations only from already authenticated non-resident $MFT::$DATA extent metadata; no blind volume scan.
3. Validates each candidate FILE record's fixup array, record number, in-use state, sequence number, base-file reference, attribute ID, type, name and LowestVCN.
4. Rejects cycles, duplicate references, conflicting candidates, gaps, overlaps, sparse runs and any physical cluster overlap.
5. Enforces explicit maximum bytes, record count, extent count, run count, recursion depth and total parsing work.
6. Never trusts an extent until the record containing it has passed all identity and structural checks.
7. Is tested with generated images covering continuation inside/outside the initial map, fragmented runs, stale sequence, wrong base reference, duplicate/cyclic entries, malformed mapping pairs, VCN gaps and resource-limit exhaustion.
8. Preserves the original validated map on any failure; no partial extension is returned.

## Manual acceptance still pending

- [ ] Keyboard-only navigation and visible focus.
- [ ] Narrator/live-region announcements.
- [ ] 100%, 125%, 150%, 200% scaling and constrained work-area checks.
- [ ] Manual generated-image smoke tests with before/after fixture SHA-256 comparison.
- [ ] Disposable Windows VHDX host validation.
- [ ] Positive deleted-file export on supported Windows storage with complete physical identity metadata.

## Safety

All automated tests use generated images and disposable VHDX only. Physical-device partition scanning/recovery remains disabled. No formatting, initialization, repair, repartitioning, mounting, partition-table writes or source-media writes. Experimental physical acquisition remains a separate guarded read-only image-copy workflow, not partition recovery.
