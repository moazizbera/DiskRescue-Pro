# DiskRescue Pro — PR #18 accessibility milestone (2026-11-10)

## Mainline integration

- [x] PR #18 merged by squash: https://github.com/moazizbera/DiskRescue-Pro/pull/18
- [x] Merge commit: 1c6205ba0bd54dc9190a0a2b816a2b885be84ce8
- [x] Exact PR head: 6827e8718e35819bb43d678faf881c1d39fb65bf
- [x] Exact-head pull-request CI: [run 42778049492](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42778049492) — successful.
- [x] Exact-head push CI: [run 42778043019](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42778043019) — successful.
- [x] Windows workflow completed restore, build, test, generated-image CLI/recovery smoke tests, WPF build, Windows x64 desktop publish, actual WPF dashboard screenshot capture, diagnostics/artifact uploads, and CLI publish.
- [x] Downloaded and visually reviewed the screenshot artifact from exact-head run 42778049492. It shows the dark image-inspection workspace, read-only source indicator, partition navigator, and scrollable inspector. This is a CI screenshot, not manual Windows interaction evidence.
- [ ] Post-merge mainline CI run 42779738791 is in progress at the time of this update; verify its final conclusion before marking mainline validation complete.

## Accessibility implementation now merged

- [x] High-contrast keyboard focus visuals for key interactive controls and navigator.
- [x] Accessible names and task-specific tooltips for primary image, partition, candidate, NTFS, export, queue, report, and experimental acquisition controls.
- [x] Polite UI Automation live regions for application, scan, and recovery queue status.
- [x] Explicit UI Automation LiveRegionChanged event notifications on status text changes; LiveSetting=Polite alone only declares intent.

## Outstanding acceptance checklist — not yet manually verified

- [ ] Keyboard-only navigation through tabs, tree, grids, buttons, cancellation, and recovery queue.
- [ ] Windows Narrator or another screen reader announces application, scan, and recovery queue progress correctly.
- [ ] Focus indicators remain visible on all dark surfaces without obscuring control content.
- [ ] Verify 100%, 125%, 150%, and 200% display scaling and common/constrained work-area sizes.
- [ ] Complete the manual Windows smoke-test procedure using generated disposable images and compare fixture SHA-256 values before/after.
- [ ] Verify empty, loading, scanning, cancelled, warning, and failed states interactively.
- [ ] Positive deleted-file export on supported Windows storage with complete physical identity metadata remains unverified; hosted CI intentionally fails closed when that metadata is unavailable.
- [ ] Run the opt-in VHDX harness on a disposable Windows VHDX host; do not infer this from hosted CI.
- [ ] Signed installer, versioned release workflow, and broader Windows-version/usability testing remain future release work.

## Safety boundary

- Automated testing continues to use generated disk images and disposable VHDX only.
- Physical-device partition scanning/recovery remains disabled.
- Experimental physical acquisition is a separate guarded read-only image-copy workflow; it is not physical-device partition recovery.
- Never format, initialize, repair, repartition, mount, or write to source media.
- Keep recovery output on a separately verified physical destination. Use a hardware write blocker for irreplaceable/evidentiary media.
- A successful export hash identifies exported bytes; without a known original hash it does not prove original content integrity.
- CI screenshots do not establish manual keyboard, screen-reader, DPI, Windows hardware, or physical HDD recovery validation.

## Next highest-priority work

1. Verify post-merge mainline CI run 42779738791.
2. Execute and record the manual Windows accessibility and responsive-layout acceptance checks on a disposable test environment.
3. Continue NTFS $ATTRIBUTE_LIST continuation-record bootstrap and MFT extent-map integration, with generated adversarial fixtures and strict resource bounds.
4. Complete safe file-entry metadata/search and deeper candidate comparison only after the above correctness and UX gates remain green.
