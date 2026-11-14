# Current status update — 2026-11-12 (unterminated storage identity strings fail closed)

## Descriptor string termination

- [x] Tightened `ParseStorageDeviceDescriptor` so identity strings without a NUL terminator before the declared descriptor boundary are ignored rather than accepted as partial vendor/model/serial values.
- [x] Updated the valid descriptor fixture to include explicit NUL terminators for vendor, product, and serial strings.
- [x] Added `StorageDescriptorParserIgnoresUnterminatedIdentityStrings`, which verifies unterminated vendor/model/serial offsets all resolve to null.
- [x] Implementation commit: `bcb614260443b935622ddae3a89cb098cf86878b`; test commit: `2638317551061def4924d0595762095f51115026`.
- [ ] Exact-head Windows CI for `2638317551061def4924d0595762095f51115026` is pending. The prior successful run [43168015244](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43168015244) validates the preceding fixed-header-offset change only, not this additional NUL-termination change.
- [ ] Confirm the new regression and updated valid-descriptor fixture pass in the exact-head TRX before accepting this change.
- [ ] This remains synthetic parser coverage, not proof of every real storage driver descriptor or physical-device inventory.
- PR #30 remains draft/open/unmerged; public physical acquisition remains hard-disabled. No original or valuable physical HDD has been opened or read; physical HDD testing is not authorized.

---

# Current status update — 2026-11-12 (storage descriptor fixed-header offset validation CI accepted)

## Defensive storage identity descriptor parsing

- [x] Tightened `WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor`: optional vendor/model/serial offsets must point to the variable-length descriptor data at or after byte 36, not into the fixed `STORAGE_DEVICE_DESCRIPTOR` header.
- [x] Added `StorageDescriptorParserIgnoresStringOffsetsIntoFixedHeader` to `tests/DiskRescue.IO.Tests/WindowsPhysicalDiskReaderTests.cs`. The regression supplies offsets 12, 35, and 1 and confirms all three parsed identity strings are null.
- [x] Implementation commit: `fcad75b2498c794c9bf1aebdee8183e62bb4e4af`; regression test commit: `37c481ac8782851dbdb8040140698a0093f1710d`.
- [x] Exact-head Windows CI run [43168015244](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43168015244) completed successfully on `37c481ac8782851dbdb8040140698a0093f1710d`; the `build-and-test` job and all 43 workflow steps succeeded.
- [x] Downloaded and parsed the exact-run TRX artifact: **379 passed, 0 failed, 0 errors; 379 executed tests passed**. The new fixed-header-offset regression is explicitly recorded as **Passed**.
- [x] Disposable VHDX read-only policy/coordinator integration and NTFS ENOSPC cleanup integration passed; generated-fixture, CLI/recovery smoke tests, WPF build/screenshot capture, Windows x64 desktop/CLI publishing, and artifact uploads passed.
- [ ] This parser test validates malformed offsets in a synthetic descriptor; it does not establish the correctness of every real storage driver descriptor or physical-device inventory.
- [ ] Independent native security review, supported x86 runtime execution, broader Windows/filesystem/filter-driver compatibility, high-DPI/accessibility/manual workflow validation, and publication namespace/power-loss durability remain open.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No original or valuable physical HDD has been opened or read; physical HDD testing is not authorized.

---

# Current status update — 2026-11-12 (ambiguous physical identity inventory regression CI accepted)

## Duplicate disk-number descriptor regression

- [x] Added `PhysicalIdentityCheckRejectsAmbiguousDuplicateDiskNumberDescriptors` to `tests/DiskRescue.IO.Tests/PhysicalImageDestinationSafetyTests.cs`. Two distinct descriptors claiming physical disk number 0 must be rejected as ambiguous even when the destination descriptor is otherwise unique.
- [x] Test commit: `8a4701d6cdff624150a22abb9f867c535ef16cdd` — `test: reject ambiguous physical disk identity inventory`.
- [x] Exact-head Windows CI run [43165869645](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43165869645) completed successfully on that exact SHA. The `build-and-test` job succeeded; all 43 workflow steps completed successfully.
- [x] Downloaded and parsed the exact-run TRX artifact: **378 passed, 0 failed, 0 errors, 381 total; all 378 executed tests passed**. The new duplicate-disk-number regression is explicitly recorded as **Passed**.
- [x] Disposable VHDX read-only policy/coordinator integration and NTFS ENOSPC cleanup integration passed; generated-fixture, CLI/recovery smoke tests, WPF build/screenshot capture, Windows x64 desktop/CLI publishing, and artifact uploads passed.
- [ ] This test proves rejection of ambiguous descriptors in the identity validator; it does not prove production physical-device inventory correctness or real physical source/destination separation.
- [ ] Independent native security review, supported x86 runtime execution, broader Windows/filesystem/filter-driver compatibility, high-DPI/accessibility/manual workflow validation, and publication namespace/power-loss durability remain open.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No original or valuable physical HDD has been opened or read; physical HDD testing is not authorized.

---

# Current status update — 2026-11-11 (publication durability contract review)

## Documented Windows API review

- [x] Reviewed Microsoft's `NtCreateFile` contract: `FILE_WRITE_THROUGH` requires requested file write operations to transfer data before completion. This is a data-write contract; it does not explicitly specify a durable parent-directory name binding after a later handle-relative `NtSetInformationFile(FileRenameInformation)` operation.
- [x] Reviewed Microsoft's `CreateFileW` `FILE_FLAG_WRITE_THROUGH` remarks: the Win32 contract says NTFS flushes metadata changes (including rename operations) resulting from processing the request. The current implementation uses `NtCreateFile` plus a separate handle-relative native rename, so the Win32 statement must not be assumed to prove the same contract for this different call sequence.
- [x] Reviewed `FlushFileBuffers`: it flushes buffered information for the specified file handle; the published user-mode contract does not provide a supported, precise directory-handle access/flush sequence for the pinned directory handles used here.
- [x] Reviewed `MoveFileExW(MOVEFILE_WRITE_THROUGH)`: its documented statement is strongest for copy-and-delete moves; switching from the current handle-relative, no-replace rename to path-based movement would change the security/race model and is not an acceptable unreviewed substitution.
- [x] Decision: **no native code change** in this review. Do not add `FILE_WRITE_THROUGH` and claim full namespace/power-loss durability based only on the available `NtCreateFile` documentation. Do not replace the handle-relative no-replace operation with a path-based move without a separate threat-model and race review.
- [ ] Obtain a supported, explicit durability contract for the exact Windows API sequence and supported filesystems/storage stacks, or formally scope the product guarantee to content flush + atomic no-replace publication without claiming parent-directory power-loss durability.
- [ ] If a candidate implementation is proposed, validate crash/power-cut behavior on disposable test media and document hardware/controller cache limitations; successful API return alone is not proof of every device's physical persistence behavior.

## Primary references

- Microsoft Learn — `NtCreateFile`: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile
- Microsoft Learn — `CreateFileA/W` and `FILE_FLAG_WRITE_THROUGH`: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea
- Microsoft Learn — `FlushFileBuffers`: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers
- Microsoft Learn — `MoveFileExW`: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
- Microsoft Learn — `FILE_RENAME_INFORMATION`: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information

## Validation and safety

- [x] Documentation commit `dc2bcbb1322ed901e22bc53f017e74a50472e039` passed Windows workflow run **43067705645**: https://github.com/DiskRescue-Pro/actions/runs/43067705645.
- [ ] Publication namespace/power-loss durability remains an open acceptance gate.
- PR #30 remains draft/open/unmerged; public physical acquisition remains hard-disabled.
- Physical HDD testing remains unauthorized. All storage integration evidence remains generated-fixture and disposable-VHDX evidence.

---

# Current status update — 2026-11-11 (directory handle-contention compatibility check)

## Existing DELETE-capable directory handle

- [x] Added `NativeSessionFailsClosedWhenExistingDirectoryHandleRequestsDeleteAccess` using a generated temporary directory and a native read-only test handle requesting DELETE access.
- [x] The test verifies the pinned session refuses the conflicting open rather than falling back to a weaker share mode, then opens successfully after the conflicting handle is released.
- [x] Exact-head Windows CI run **43067171706** passed all 24 workflow steps on `e9e23698b2eead2cc1513ca678582e8b71e9840e`: https://github.com/DiskRescue-Pro/actions/runs/43067171706.
- [x] Latest test artifact: **377 passed, 0 failed, 3 skipped (380 total)**. All executed tests passed (377/377); three tests were skipped.
- [x] Disposable VHDX read-only and NTFS ENOSPC integrations, generated fixture/CLI/recovery smoke tests, WPF build, dashboard screenshot, Windows x64 desktop/CLI publishing, and artifact uploads passed.

## Compatibility interpretation

This is one deterministic share-mode contention test on the hosted Windows runner. It does not certify compatibility with every antivirus, indexing, backup, synchronization, or filesystem filter driver, nor across every supported Windows/filesystem combination. The required policy remains fail closed; do not fall back to `FILE_SHARE_DELETE`.

## Safety boundary

- PR #30 remains draft/open/unmerged.
- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read. No physical HDD testing is authorized.

---

# Current status update — 2026-11-11 (constructor and cancellation cleanup gates)

## Constructor failure and cancellation review

- [x] Reviewed the native destination-session constructor and partial directory-chain open path.
- [x] Changed construction so each successfully opened directory handle is registered for cleanup before component validation.
- [x] Construction failure now uses the shared all-resources disposal helper, attempts every retained handle in reverse order, and preserves the original failure alongside any cleanup failures.
- [x] Existing Windows regression still covers a later missing path component after an ancestor handle has been opened; the ancestor can be renamed immediately after the failed open.
- [x] Added `WriterDeletesPartialImageWhenCancellationOccursDuringCopy`: cancellation after the first source read must remove the unpublished temporary image and publish neither image nor manifest.
- [x] The writer's post-image-commit behavior remains intentional: once the verified image is published, manifest creation/publication uses `CancellationToken.None` so late cancellation does not abandon a committed image without its sidecar.
- [x] Cancellation regression CI run **43066365462** passed all 24 workflow steps: https://github.com/DiskRescue-Pro/actions/runs/43066365462.
- [x] Constructor cleanup exact-head CI run **43066527404** passed all 24 workflow steps on `fc37877a762edc5adb0082edc7ec2ef289dea0d5`: https://github.com/DiskRescue-Pro/actions/runs/43066527404.
- [x] Latest test artifact: **376 passed, 0 failed, 3 skipped (379 total)**. All executed tests passed (376/376); skipped tests are not counted as passes.
- [x] Both runs also passed disposable VHDX read-only policy and NTFS ENOSPC cleanup integrations, generated-fixture/CLI/recovery smoke tests, WPF build, screenshot capture, Windows x64 desktop/CLI preview publishing, and artifact uploads.

## Remaining acceptance gates

- [ ] Existing-handle contention and compatibility with antivirus, indexing, backup, synchronization, and other filesystem filter software.
- [ ] Supported Windows/filesystem matrix and automated x86 execution/ABI validation.
- [ ] Publication power-loss durability of the final rename and directory metadata; content flush alone does not establish this.
- [ ] Independent security review.
- [ ] Manual keyboard/accessibility, high-DPI, and multi-work-area UI validation.
- [ ] Physical HDD testing remains unauthorized and must not be attempted.

## Safety boundary

- PR #30 remains draft/open/unmerged.
- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read. All storage integration evidence remains disposable VHDX and generated-fixture evidence.

---

# Current status update — 2026-11-11 (directory-chain rename exclusion CI accepted)


### Additional failure-path test — Windows CI passed

- [x] Added and executed `NativeSessionReleasesPartiallyOpenedDirectoryChainWhenLaterComponentIsMissing`. It forces a later path component to be missing after an ancestor has been opened, then verifies the ancestor can immediately be renamed, demonstrating that the partial chain was released.
- [x] Test commit: `d3d6bdf1238b9c7b6f955028bbad85594af040a6`; Windows CI run **43059470350** passed: https://github.com/DiskRescue-Pro/actions/runs/43059470350.
- [x] Build: 0 warnings, 0 errors. Standard suite: **374 passed, 0 failed, 3 skipped (377 total)**.
- [x] Separate disposable read-only VHDX policy and coordinator integration tests passed; separate genuine NTFS destination-full integration passed. These remain synthetic VHDX tests, not physical HDD validation.
- [ ] Constructor failure, cancellation, acquisition-failure cleanup, and compatibility with existing handles/filter drivers still need focused review and testing.

## Exact-head Windows validation

- [x] Exact-head Windows CI #43046744037 passed on `596ae34f2b2ca9360864d4f47bce9d504b65ad90`: https://github.com/DiskRescue-Pro/actions/runs/43046744037.
- [x] Release build succeeded with 0 warnings and 0 errors.
- [x] Standard test suite: **373 passed, 0 failed, 3 skipped (376 total)**.
- [x] The disposable read-only VHDX policy and coordinator integration tests passed; the separate NTFS destination-full integration passed; DiskPart detached the disposable virtual disks.
- [x] The earlier implementation-only commit `109de25c` failed because the old regression test still expected the directory rename to succeed. The failure was at that exact assertion (372 passed, 1 failed, 3 skipped). The test was replaced with final-directory and ancestor rename-denial assertions; the complete checklist-head workflow then passed.
- [ ] The CI evidence is hosted Windows runner evidence only. It does not establish compatibility across all Windows versions/filesystems or third-party filter drivers.

## Current acceptance interpretation

- [x] The new Windows test proves that on the hosted runner, attempts to rename the final destination directory and its ancestor fail while the session is open, publication succeeds in the requested directory, and a rename succeeds after session disposal.
- [ ] Existing-handle contention and compatibility with antivirus, indexing, backup, synchronization, and other filesystem filter software still need targeted compatibility testing.
- [ ] Constructor failure, partial path-open failure, cancellation, and acquisition-failure cleanup paths still need focused review/tests for retained chain handles.
- [ ] Independent security review remains outstanding.
- [ ] Publication power-loss durability and filesystem compatibility remain separate unresolved gates; rename exclusion does not solve them.

## Safety boundary

- PR #30 remains draft/open/unmerged.
- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read. No physical HDD test is authorized.

---

# Current status update — 2026-11-11 (directory-chain rename exclusion implementation)

This is an implementation candidate, not a release approval. Windows CI must validate the new sharing contract before it can be accepted. Keep public physical acquisition hard-disabled.

## Change made

- [x] The native destination session now retains every opened directory component from the local drive root through the requested destination for the session lifetime.
- [x] Non-root directory components are opened without `FILE_SHARE_DELETE`; the handles remain open until session disposal. This is intended to prevent a concurrent rename/delete of the final destination directory or any ancestor while acquisition is active, rather than relying on repeated point-in-time path checks.
- [x] The root volume handle remains retained too; the volume root is not an ordinary renameable directory component.
- [x] Replaced the prior test that deliberately renamed the pinned directory with a deterministic Windows test that attempts to rename both the final directory and an ancestor, expects both attempts to fail while the session is active, verifies publication remains at the requested path, and verifies rename works again after disposal.
- [x] Exact-head Windows CI #43046744037 passed on checklist head `596ae34f2b2ca9360864d4f47bce9d504b65ad90`: build succeeded with 0 warnings/errors; standard suite **373 passed, 0 failed, 3 skipped (376 total)**; the Windows native directory-session tests executed as part of that suite. Run: https://github.com/DiskRescue-Pro/actions/runs/43046744037.
- [x] The opt-in disposable-VHDX workflow stages also passed: the read-only attached VHDX policy test and coordinator integration both passed, the separate NTFS destination-full integration passed, and DiskPart detached the disposable virtual disks. This is synthetic VHDX evidence, not physical-device acceptance.
- [x] The implementation-only commit `109de25c` initially failed because the old test still expected a directory rename to succeed; the log shows **372 passed, 1 failed, 3 skipped**, failing exactly at the now-prohibited rename. The test was then replaced with the new final-directory/ancestor denial test in `1ac41f05`; the subsequent exact-head CI #43046744037 passed. Earlier failure: https://github.com/DiskRescue-Pro/actions/runs/43046626051.
- [ ] Compatibility remains a deliberate open gate: a directory component may fail to open if an existing process holds a conflicting handle that requested DELETE access. Evaluate behavior with common antivirus/indexer/backup software and on supported Windows/filesystem combinations. Fail closed; do not fall back to share-delete behavior.
- [ ] Verify cleanup/disposal under all constructor and partial-open failure paths, plus cancellation and acquisition failure paths.
- [ ] Independent review must confirm Windows directory rename/share semantics for both final and ancestor components, including filesystem-specific behavior.
- [ ] Do not infer filesystem durability from rename exclusion. The separate power-loss publication durability gate remains open.

## Safety boundary

- PR #30 remains draft/open/unmerged.
- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read. No physical HDD test is authorized.

---

# Current status update — 2026-11-11 (directory-name stability race review)

This is a source-review finding, not a confirmed exploit and not a code change. Keep the physical-acquisition release gate closed pending design and tests.

## Additional path-stability race to resolve

- [x] Re-read the complete coordinator flow and native directory-open/share contract at exact head `777aee8d71b46e830d10e43d3d36efef42673ae0`.
- [x] Confirmed the coordinator checks that the pinned directory handle still resolves to the requested path immediately before source-sector reads. Publication remains relative to the pinned directory handle, so a replacement path cannot redirect writes into the replacement directory.
- [ ] The final path comparison is still a point-in-time check: the final directory and its ancestors are opened with `FILE_SHARE_DELETE`, and intermediate directory handles are released while walking the path. A concurrent rename of the pinned directory or an ancestor after the last comparison can move the pinned destination during acquisition. The writer will continue targeting the original directory object, but its pathname may no longer be the requested destination path. This is an availability/location-integrity race, not evidence that the replacement directory receives the image.
- [ ] Design whether the supported contract should prevent renames for the session lifetime by retaining directory-chain handles opened without `FILE_SHARE_DELETE`, or whether a different explicit destination-path semantics is preferable. Evaluate compatibility with antivirus/indexer handles, existing open directories, cleanup, and normal user workflows before choosing.
- [ ] Add deterministic Windows tests for attempted rename of the final directory and an ancestor while a session is active; prove either that the rename is denied for the session lifetime or that the chosen alternative fails closed without publishing to an unexpected path.
- [ ] Do not treat repeated path comparisons as eliminating this race; a check followed by a later operation is not an atomic name-stability guarantee.
- [ ] Re-run the hosted Windows test suite and disposable-VHDX integrations after any implementation change. Keep public physical acquisition hard-disabled until independent review accepts the final semantics.

## Review scope and safety

The source review also reconfirmed that the session is sequential/non-thread-safe, the temporary file is held with restrictive write/delete sharing, and image/manifest publication uses handle-relative no-replace rename. The current content flush does not establish power-loss durability of the rename or directory metadata.

No source code or physical media was changed or accessed during this review.

---

# Current status update — 2026-11-11 (exact-head CI #43033267693)

This update records the completed validation of the current PR head. It does not change the release or physical-media safety boundary.

## Exact-head CI

- [x] PR #30 head `843ddd4a6a2e9fa8dd144060758132a8bed7ac83` completed Windows CI successfully: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43033267693.
- [x] Standard IO suite: **373 passed, 0 failed, 3 skipped (376 total)**.
- [x] The separate read-only VHDX policy smoke test passed; public physical acquisition remained blocked.
- [x] The separate coordinator/VHDX integration passed: generated disposable source VHDX was read-only; copied image bytes, SHA-256, manifest publication, and before/after source hash invariance were verified using synthetic inventory metadata and the real pinned native destination adapter.
- [x] The separate NTFS ENOSPC integration passed after measuring **85,762,048 bytes** free against a **134,217,728-byte** source image. Windows disk-full was reproduced; no final image or manifest remained, the unpublished partial image was cleaned up, and the source hash was unchanged. Both VHDXs detached successfully.
- [x] Generated-fixture discovery, CLI/recovery smoke tests, WPF build and screenshot capture, Windows x64 desktop/CLI publishing, and artifact uploads passed.
- [ ] These disposable-VHDX tests do not validate production physical hardware identity reconciliation or authorize physical HDD acquisition.

## Native adapter review checkpoint

- [x] Source-level review covered handle-relative create/open, reparse-point rejection, handle-derived identities, SafeHandle lifetime ordering, restrictive sharing, delete disposition, and pinned-directory no-replace rename.
- [x] Native structure and variable-length rename-buffer offsets were manually checked for x86/x64 layouts.
- [ ] Automated x86 execution and broader Windows/filesystem compatibility testing remain outstanding; hosted CI currently proves the Windows x64 path.
- [ ] Independent security review remains outstanding. This source review is not a third-party audit.
- [ ] Publication still requests file-content flush before atomic rename; power-loss durability of rename/directory metadata is not established. Keep the publication durability and filesystem compatibility gates open.
- [ ] Manual keyboard/accessibility, high-DPI, and multi-work-area UI checks remain outstanding.

## Safety boundary

- PR #30 remains draft/open/unmerged.
- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read. No physical HDD test is authorized by this validation.

---

# Current status update — 2026-11-11 (native adapter static review checkpoint)

This section records a bounded source review after exact-head CI #1334. It is not an independent third-party security audit or release approval.

## Review completed so far

- [x] Reviewed the native adapter's handle-relative open/create flow, path-component validation, FILE_OPEN_REPARSE_POINT use, reparse attribute rejection, handle-derived volume/directory/file identities, no-replace rename layout, delete disposition, and SafeHandle reference acquisition around raw parent-handle use.
- [x] Manually checked the native structure layouts and offsets against the documented Windows ABI shapes for both pointer widths: UNICODE_STRING, OBJECT_ATTRIBUTES, FILE_ID_INFO, FILE_ATTRIBUTE_TAG_INFO, IO_STATUS_BLOCK, and the variable-length FILE_RENAME_INFORMATION buffer. This is a source-level review only; automated architecture-specific ABI assertions and Windows-version/filesystem coverage are not yet present.
- [x] Confirmed the implementation explicitly documents sequential, non-thread-safe session use and that the coordinator uses the session sequentially. SafeHandle references protect individual native calls; they do not make concurrent session operations or concurrent disposal supported.
- [x] Confirmed the rename uses the pinned destination directory handle, requests no-replace semantics, and the opened file handle has DELETE access. The directory handle is held alive across the native rename call.
- [ ] Add focused native ABI/layout tests (including x86/x64 expectations where supported) and broaden adapter tests beyond the current hosted Windows runner.
- [x] Identified a concrete publication-durability gap for further remediation: the destination file is opened with FILE_SYNCHRONOUS_IO_NONALERT but not FILE_WRITE_THROUGH, and FlushToDiskAsync is called before (not after) the handle-relative rename. Microsoft's CreateFile documentation says FILE_FLAG_WRITE_THROUGH causes NTFS to flush metadata changes, including rename operations, resulting from processing the request; NtCreateFile documents the corresponding FILE_WRITE_THROUGH create option (0x00000002). This does not establish the same guarantee for every supported filesystem, nor does it prove the current rename is lost after a crash; it means the current code does not request the documented NTFS write-through behavior for the rename. References: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew and https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile.
- [ ] Decide and implement a performance-conscious durability contract for publication. Evaluate whether a write-through open/mode around the rename can be used without forcing every large image write to synchronous media, and constrain/document supported filesystems accordingly. Do not claim power-loss durability for the published name until this is resolved and tested.
- [ ] Do not assume a user-mode directory-handle flush is a portable fix: Microsoft documents FlushFileBuffers primarily for file handles and its volume-wide mode has privilege constraints; a supported ordinary-user parent-directory flush sequence has not been established.
- [ ] Complete independent security review of native ABI/layout, handle lifetime, reparse handling, rename semantics, filesystem compatibility, and race assumptions.
- [ ] Disposable-VHDX coordinator end-to-end acquisition, source-byte invariance, output image/hash/manifest verification, destination-full handling, and full cleanup-path acceptance remain outstanding.
- [ ] Manual Windows keyboard/accessibility, high-DPI, and multi-work-area checks remain outstanding.

## Current exact-head validation

- [x] Exact-head Windows CI #1334 passed on d1b14884f0da452f46125651d446ca41391818a4: 370 passed, 0 failed, 1 skipped (371 total). Disposable RAW VHDX attached read-only, filtered public-API policy test passed (1/1), and DiskPart detached it successfully. Generated fixtures, CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and artifact uploads passed. https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43000070523
- PR #30 remains draft/open; public physical acquisition remains hard-disabled. No original or valuable physical HDD has been opened or read. These results do not establish physical HDD recovery readiness.

---

# Current status update — 2026-11-11 (pinned destination identity regression validated)

This update supersedes the pending validation note in the preceding 2026-11-11 section.

## Preflight-to-pin directory replacement finding

- [x] The coordinator now opens and pins the requested destination directory immediately after validating the requested image path and before path-based destination preflight.
- [x] It retains the same destination session through preflight and revalidates the directory path resolved from the pinned handle after the second preflight and again after final pre-read revalidation, before the first source-sector read.
- [x] The deterministic regression asserts the replacement is rejected before the source is opened: zero environment source-open calls, zero source read attempts, zero source disposals, and no output files.
- [x] Exact-head Windows CI #1334 passed on test commit `d1b14884f0da452f46125651d446ca41391818a4`: **370 passed, 0 failed, 1 skipped (371 total)**. The VHDX policy smoke test also passed with a disposable RAW VHDX attached read-only as PhysicalDrive2, filtered host test passed (1/1), and DiskPart detached it successfully. Generated fixture, CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and artifact uploads all completed. Run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43000070523.
- [x] CI #1333 on implementation-only commit `ab7ecc8c8d9417207ff67a4845ac39a3f8179368` exposed a stale regression expectation: the test still expected one source-open call, while the new earlier destination-session pinning correctly produced zero. The failure was **1 failed, 369 passed, 1 skipped**; no later workflow stages ran. The test expectation was then corrected in `d1b14884`, and exact-head CI #1334 passed. Run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43000040610.
- [ ] The fix is validated by the deterministic coordinator test and hosted CI, but independent security review of native adapter ABI/layout, handle lifetime, reparse handling, rename semantics, and remaining race assumptions is still outstanding.

## Safety and release boundary

- [ ] Disposable-VHDX coordinator end-to-end acquisition, source-byte invariance, output image/hash/manifest verification, destination-full handling, and full cleanup-path acceptance remain outstanding.
- [ ] Manual Windows keyboard/accessibility, high-DPI, and multi-work-area checks remain outstanding.
- Public physical acquisition remains hard-disabled. No original or valuable physical HDD has been opened or read. These CI results do not establish physical HDD recovery readiness.

---

# Current status update — 2026-11-11 (VHDX public-API gate regression)

This is the current status snapshot for PR #30. Earlier dated sections are historical evidence and must not override this section.

## Candidate and exact-head CI

- PR #30 remains **draft/open**: https://github.com/moazizbera/DiskRescue-Pro/pull/30
- Branch: `security/managed-destination-session-contract-20261110`
- Last implementation head validated by Windows CI #1318: `133305a9377b5ddb75829c4b3200f250e92a80d6`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42996738462
- New VHDX API-gate regression commit: `4fe9618155befa306648417340cbd7cf22d0cb43`.
- CI workflow now runs `tools/Test-WindowsVhdxSafety.ps1` on the Windows runner after the normal test suite, so the opt-in VHDX check is executed rather than remaining skipped in the ordinary test invocation.
- Workflow integration commit: `06d8b5223cc3ed300039c47316c04109d43d87a2`.
- CI #1325 on that commit and CI #1326 on checklist head `005eb428` both failed in the new DiskPart VHDX setup step before the host integration test ran. The regular suite still passed **370/0/1** in both runs. Failure was `diskpart` exit `-2147024809` while processing the create/attach script; no VHDX acceptance result is claimed. CI #1325: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42997905179 ; CI #1326: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42997947249.
- Harness correction commit `7b89209ced2bbebab5fa594c836ad275872a46f8` simplified the VHD creation command and captured DiskPart output. CI #1327 still failed before attachment: DiskPart printed its command-help screen and exited `-2147024809`; the filtered host test did not run. The ordinary suite passed **370/0/1**. Run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42998250762.
- Diagnosis: the script wrote DiskPart command files as UTF-16LE (`Set-Content -Encoding Unicode`). The runner's DiskPart parser treated the encoded file as invalid commands and emitted help. The harness now writes explicit ASCII command files and rejects non-ASCII temp paths rather than silently altering them. Fix commit: `a3235708f1fefd5fd1ddff41b689d260253e14d4`; exact-head CI is still required.
- Documentation updates for this VHDX gate: `4a3518ebaef246413a53155622d3f2d891ddff15`.
- Test result on implementation head `133305a9`: **370 passed, 0 failed, 1 skipped (371 total)** in `DiskRescue.IO.Tests.dll`.
- CI #1318 also completed the workflow's generated disk-image fixtures, CLI discovery/recovery smoke tests, deleted-NTFS extraction and manifest checks, one-command lost-NTFS harness, WPF build, Windows x64 desktop/CLI publishing, dashboard screenshot capture, and artifact uploads.
- CI #1317 passed on the immediately preceding checklist/session-evidence commit `6a36ef759aead0cad0abc380b21eaf28d34be4f2`: **370 passed, 0 failed, 1 skipped (371 total)**. https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42996617539
- CI #1316 also passed on its earlier exact head with the same 370/0/1 result. The latest exact-head run #1318 is the current acceptance evidence.

## Disposable-VHDX policy smoke-test update

- [x] The opt-in Windows VHDX smoke test still attaches only a newly created RAW virtual disk with DiskPart's `readonly` flag and confirms Windows reports it as read-only.
- [x] The test confirms the discovered storage bus is Virtual (14) or File-backed Virtual (15), and the production acquisition assessment blocks it as virtual/layered.
- [x] The test now calls the public `PhysicalDiskImageAcquisition.AcquireAsync` only to assert immediate `NotSupportedException` rejection; it verifies that neither the image nor manifest is created.
- [x] The test explicitly performs no source-sector reads and does not invoke the internal coordinator or low-level physical-disk reader.
- [ ] This is **not** disposable-VHDX imaging, native writer + VHDX source integration, or coordinator end-to-end acceptance. Those remain separate release gates; no virtual-device policy is bypassed.
- [x] Exact-head CI #1329 and checklist-head CI #1330 both confirm the host VHDX policy smoke test: DiskPart created and attached the disposable RAW VHDX; the script reported `Disposable VHDX attached read-only as PhysicalDrive2`; the filtered host test passed (1 passed); DiskPart detached successfully. Both runs also passed the normal suite (370 passed, 0 failed, 1 skipped) and completed all later workflow stages. CI #1329: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42998687460 ; CI #1330: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42998723208.
- [x] Checklist-head CI #1331 passed on this checklist commit: **370 passed, 0 failed, 1 skipped**; the disposable VHDX was created, attached read-only, passed the filtered policy test, and detached successfully. Generated fixtures, CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and artifact uploads also completed. Run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42999447983.
- [ ] This remains a **policy smoke test only**. It does not perform source-sector reads, exercise the internal coordinator, copy a VHDX into a destination, prove source bytes unchanged across acquisition, or validate image/hash/manifest publication. Do not treat it as disposable-VHDX acquisition end-to-end acceptance.

## Native destination writer evidence

- [x] Native Windows session ABI layout regression tests pass in exact-head CI.
- [x] Native session + destination writer integration test publishes a generated 384-KiB in-memory source fixture, then verifies the committed image bytes/hash and manifest after disposing the session.
- [x] SafeHandle lifetime ordering is covered by the corrected add-reference-before-get-handle implementation.
- [x] Session file handles deny competing write/delete opens while the owning session is active.
- [x] Sequential-use contract is documented on the native session: callers must serialize session operations and must not dispose the session while an operation or owned-stream write is in progress; the coordinator currently uses it sequentially. Concurrent session calls/disposal remain unsupported, not thread-safe.
- [ ] The native writer integration test is **not** a VHDX source-device test and does not constitute a complete physical acquisition coordinator end-to-end test.

## Remaining release gates — still blocking

- [ ] Independent security review of native P/Invoke layouts, handle lifetime, reparse handling, no-replace rename, and race assumptions.
- [x] **Review finding — preflight-to-pin directory identity (code change pending exact-head CI):** destination session creation now occurs immediately after validating the requested image path and before any path-based destination preflight. The coordinator retains that pinned directory handle and checks its resolved path after the second preflight and again after final pre-read revalidation, before the first source-sector read. This closes the identified window in which a same-volume directory replacement between path assessment and pinning could redirect publication. The existing deterministic path-replacement regression now asserts that source opening itself is prevented. Implementation commit `ab7ecc8c8d9417207ff67a4845ac39a3f8179368`; test assertion commit `d1b14884f0da452f46125651d446ca41391818a4`. Await exact-head CI before marking the finding validated.
- [ ] Disposable-VHDX coordinator end-to-end acceptance using a newly created disposable virtual disk and a separate disposable destination; do not bypass production virtual-device rejection.
- [ ] Prove disposable source bytes remain unchanged and output bytes/hash/manifest are correct.
- [ ] Exercise destination-full/insufficient-space behavior and cleanup on disposable media.
- [ ] Exercise cancellation, source read failure, manifest-publication failure, path replacement, and cleanup on the full disposable-VHDX path. Existing coordinator unit tests cover cancellation, read failure, manifest failure, and path replacement with test doubles; this is not the same as VHDX end-to-end evidence.
- [ ] Manual keyboard-only, accessibility/Narrator, high-DPI, and multi-work-area interaction checks remain outstanding.
- [ ] Only after all gates pass, conduct a separate design/security review before any proposal to enable the public acquisition API/UI.

## Non-negotiable safety boundary

- Public physical acquisition remains hard-disabled.
- No original or valuable physical HDD has been opened or read.
- Never format, initialize, repair, repartition, mount, or write to source media.
- Automated tests use generated disk images and explicitly disposable VHDX fixtures only.
- Do not claim physical HDD readiness based on CI, synthetic images, or a WPF screenshot.

---

# Current status update — 2026-11-10 (PR #30 integrated pinned-destination coordinator)

This section is the current source of truth for physical acquisition safety. Older dated sections below are retained as historical snapshots and may describe earlier states of PR #30; do not use their old unchecked items as the current implementation status.

## Current candidate

- [PR #30 — managed pinned-destination session contract and coordinator integration](https://github.com/moazizbera/DiskRescue-Pro/pull/30) remains **draft/open**.
- Branch: `security/managed-destination-session-contract-20261110`.
- Exact latest PR head (including checklist/screenshot-review update): `4b0a2945d5c1c2f60d2b8cc1025ccbe6e4cbbf3b`.
- Related security issue: [Issue #23 — pin and revalidate acquisition destination filesystem identity](https://github.com/moazizbera/DiskRescue-Pro/issues/23).

## Implemented and exact-head validated

- [x] Managed destination-session contract with directory and file identities.
- [x] Native Windows session opens local fixed-drive path components relative to already-pinned parent handles; rejects reparse points rather than following them.
- [x] Handle-relative create-new, file-ID query, durable flush, unpublished-file deletion, and no-replace publication.
- [x] Stream-based image writing and same-open-stream SHA-256/length verification.
- [x] Image and manifest temporary files are created and published through the same pinned session.
- [x] Internal coordinator repeats source/boot/volume/destination preflight, opens the source read-only, and revalidates source identity before any source-sector read.
- [x] Coordinator reconciles the volume GUID obtained from the pinned destination handle against the complete volume map and preflight destination extents.
- [x] Coordinator checks physical-disk identity separation and aborts if the pinned volume maps to the source disk.
- [x] Coordinator compares the path resolved from the pinned directory handle with the requested directory and aborts on a path replacement detected before source reads.
- [x] Regression tests assert zero source reads for pinned-volume mismatch and changed-directory-path cases.
- [x] Manifest publication failure preserves the committed image and returns an explicit manifest-commit error.
- [x] Native Windows tests cover normal directory operations, path replacement after pinning, no-overwrite publication, handle deletion, symbolic-link rejection, and junction rejection where fixture creation is permitted.
- [x] Exact-head Windows CI #1305 passed at `4b0a2945d5c1c2f60d2b8cc1025ccbe6e4cbbf3b`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42883203138
- [x] CI #1305 test project result: 367 passed, 0 failed, 1 skipped (368 total).
- [x] Visually reviewed the CI #1303 WPF screenshot artifact (963×662): read-only source state, selected MBR partition, navigator, and inspector are visible; lower inspector metadata requires scrolling.
- [x] CI #1303 also passed generated fixture creation, CLI smoke tests, deleted-NTFS extraction/recovery-manifest tests, one-command lost-NTFS recovery harness, WPF build, Windows x64 desktop/CLI publishing, dashboard screenshot capture, and artifact uploads.

## Explicit compatibility boundary

- [x] Native destination support is currently limited to local fixed drive-letter paths.
- [x] UNC/network paths, mapped drives, volume-GUID input paths, volume mount points, junctions, symbolic links, and other reparse points are rejected/fail closed. They are not silently followed.
- [x] Unsupported file identity or native filesystem operations fail closed.
- [x] The internal coordinator has no path-based image/manifest fallback.
- [x] Public physical acquisition remains hard-disabled: `PhysicalDiskImageAcquisition.IsAcquisitionEnabled == false`; the public API rejects before opening a physical source or creating output.
- [x] No physical HDD was opened or read for this work.

## Remaining release gates — do not bypass

- [ ] Independent security review of native P/Invoke layouts, handle lifetime, reparse handling, no-replace rename, and race assumptions.
- [ ] Broader Windows/filesystem compatibility matrix; current CI is one hosted Windows runner and is not a compatibility certification.
- [ ] Disposable-VHDX end-to-end acquisition acceptance using only a newly created, disposable virtual disk and a separate disposable destination.
- [ ] Prove source bytes remain unchanged and output bytes/hash/manifest are correct throughout the disposable test.
- [ ] Exercise cancellation, read failure, destination-full, manifest-publication failure, path replacement, and cleanup on disposable media.
- [x] Visually reviewed the CI #1303 WPF screenshot artifact (963×662): it shows the read-only source state, validated MBR partition selection, navigator, and inspector. The inspector viewport is intentionally scrollable but clips some lower metadata until scrolled; this is a visual limitation, not a data-integrity failure.
- [ ] Manual keyboard-only, accessibility/Narrator, high-DPI, and multi-work-area interaction checks remain outstanding.
- [ ] Only after all gates pass, conduct a separate design/security review before any proposal to enable the public acquisition API/UI.
- [ ] Physical HDD testing remains unauthorized until a separate release decision; never test against an original or valuable disk.

## Operational safety rules

- Never format, initialize, repair, repartition, mount, or write to source media.
- Never use an original or valuable physical HDD as a test target.
- Automated tests use generated disk images and explicitly disposable VHDX fixtures only.
- Keep source handles read-only; software read-only is not a hardware write blocker.
- Keep acquisition/recovery output on a separately verified physical device.
- Do not claim physical HDD readiness based on CI, synthetic images, or a WPF screenshot.

---

# Current status update — 2026-11-10 (native Windows session CI evidence)

## Native adapter validation

- [x] Windows build passed in CI #1266 at `a2419efe42f515834be6270134c71950e90e1d93`.
- [x] All 361 tests in the Windows test project passed in CI #1266; one pre-existing disposable-VHDX test was skipped by its opt-in guard.
- [x] Generated fixture, CLI discovery/extraction/recovery smoke tests passed.
- [x] WPF build, Windows x64 desktop preview publish, dashboard screenshot capture, and CLI preview publish passed in CI #1266.
- [x] Follow-up CI #1267 at `1b6de65d12909768f2ad6a2141370c259c957159` passed build, test, generated-fixture checks, CLI/recovery smoke tests, and WPF build; preview publication was still running at last inspection.
- [ ] Latest code head `32beebebff094ff98acacfac0efb86432ebe0aed` is under CI #1268: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42878590558. This commit adds explicit SafeFileHandle disposal alongside FileStream disposal; do not mark it validated until this exact-head run finishes.
- [x] The first Win32 rename attempt was rejected by the Windows runner with error 87. The adapter now uses `NtSetInformationFile(FileRenameInformation)`; CI #1266's native publish/no-replace tests passed with that native operation.
- [ ] No production coordinator integration.
- [ ] No destination volume-to-physical-disk reconciliation.
- [ ] No mount-point, junction, UNC, network, or volume-GUID support; unsupported paths remain fail-closed.
- [ ] No independent security review or broad Windows/filesystem compatibility matrix yet.
- [ ] Public physical acquisition remains disabled; no physical HDD was opened or read.

## Exact links

- CI #1266: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42878437053
- CI #1267: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42878535527
- CI #1268: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42878590558

---

# Current status update — 2026-11-10 (native Windows session first implementation slice)

## Native adapter work now present on PR #30

- [x] Added `WindowsPhysicalAcquisitionDestinationSession` as an internal, Windows-only adapter.
- [x] Opens a local fixed-drive root and traverses each directory component using directory-handle-relative `NtCreateFile` opens with `FILE_OPEN_REPARSE_POINT`; checks the opened handle and rejects reparse-point components instead of following them.
- [x] Creates temporary files relative to the pinned destination directory handle with `FILE_CREATE` (create-only).
- [x] Queries `FILE_ID_INFO` from the directory and each created file handle.
- [x] Flushes through the existing file stream/handle, marks unpublished files for deletion by handle, and keeps streams owned by the session.
- [x] Adds native Windows tests for create/identity/flush/publish, destination path replacement after pinning, no-overwrite publication, unpublished deletion, and symbolic-link rejection where runner permissions allow.
- [ ] No passing native-adapter CI result recorded yet. CI #1263 is running against adapter commit `97beeec84da67f779a5e973a226ccd53384ca10e`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42878101418
- [ ] A previous `SetFileInformationByHandle(FileRenameInfo)` implementation returned `ERROR_INVALID_PARAMETER (87)` on the Windows runner. The current code switches to `NtSetInformationFile(FileRenameInformation)` with a relative directory handle and no-replace semantics; this replacement is awaiting CI validation.
- [ ] Current adapter is deliberately limited to local fixed drive-letter paths. UNC/network paths, volume-GUID paths, volume mount points, symlinks, and junctions are rejected/unsupported.
- [ ] Adapter is not connected to the production coordinator. It does not yet reconcile the pinned volume with the physical volume-to-disk map.
- [ ] No physical acquisition gate change; `IsAcquisitionEnabled` remains false.

## Native adapter safety boundary

This is an early native implementation slice, not a completed acquisition pipeline. The only passing evidence remains the earlier managed-contract/fake-session CI. The native path must pass its Windows tests, then receive review and additional filesystem/race tests. Do not use an original or valuable physical HDD.

---

# Current status update — 2026-11-10 (PR #30 destination-session foundation and CI verification)

## Current candidate

- [PR #30 — managed pinned-destination session contract](https://github.com/moazizbera/DiskRescue-Pro/pull/30) remains **draft/open**.
- Branch: `security/managed-destination-session-contract-20261110`.
- Current implementation head before this checklist refresh: `2527c6793ba1565e10a6160e82ff77bfe321d272`.
- Related blocking security investigation: [Issue #23 — pin and revalidate acquisition destination filesystem identity](https://github.com/moazizbera/DiskRescue-Pro/issues/23).

## Implemented in PR #30

- [x] Internal managed destination-session contract with immutable directory and file identities (volume identity + 128-bit file ID).
- [x] Validated leaf-name boundary; the session API accepts leaf names, not arbitrary paths.
- [x] Stream-based image writer and same-open-stream SHA-256/length verifier.
- [x] Caller-owned stream manifest serialization.
- [x] Path-free writer orchestration for image and manifest through one session.
- [x] Fail closed on missing directory/file identities and wrong-volume file identities before source reads.
- [x] No-replace publication contract; cleanup of unpublished files; preserve committed image and report explicit manifest-commit failure.
- [x] Fake-session lifecycle and failure-injection tests for collisions, foreign streams, zero-read failures, cleanup, disposal, wrong-volume manifest, and manifest-failure preservation.
- [x] Native Windows API research notes for directory-relative create-new, handle-relative no-replace rename, file IDs, and reparse-point inspection.

## Exact-head CI evidence

- [x] CI #1247 passed at `d619f2ca4bbe3f907098b7d29064964c0fa9dbc3`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42867771919
- [x] CI #1253 passed at `e668d10ae4cd0c597284b7d2903d8363ba5b977b`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42872475814
- [x] CI #1254 passed at `e720b49ae1b65b252960b3d97d6860b57d729eb8`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42873048910
- [x] Final branch-head CI #1255 passed at `2527c6793ba1565e10a6160e82ff77bfe321d272`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42873137979
- [x] Final #1255 Windows job completed all 22 steps successfully, including build, tests, generated fixtures, CLI/recovery smoke tests, WPF build, Windows x64 preview publish, screenshot capture, and artifact uploads.

## Still blocked — do not mark complete

- [ ] Implement a native Windows pinned-directory session; the current PR contains no P/Invoke adapter.
- [ ] Correctly implement and test x86/x64 native structure layouts and handle ownership.
- [ ] Implement directory-relative create-new, same-handle identity/flush/delete, and handle-relative no-replace rename.
- [ ] Classify reparse tags and targets; support only validated volume mount points and reject unsafe/unvalidated redirections.
- [ ] Reconcile the pinned destination handle's volume identity with the existing volume-to-physical-disk extent map; volume serial alone is not a physical-disk identity.
- [ ] Add deterministic Windows tests for directory path replacement, symlink/junction/mount-point behavior, no-replace collisions, missing IDs, wrong-volume rejection, zero source reads, and handle disposal.
- [ ] Integrate the native session into the production coordinator only after the adapter and tests pass; no path-based fallback.
- [ ] Run disposable-VHDX safety smoke tests and independent review before any gate change.
- [ ] Complete manual Windows UI/accessibility checks and screenshot review separately from CI.

## Safety boundary

- Public physical acquisition remains hard-disabled (`IsAcquisitionEnabled == false`); the public API rejects before opening a source handle or creating output.
- The coordinator is not wired to the new session writer.
- No physical HDD was opened or read for PR #30.
- Continue using generated images and disposable VHDX only. Never test against an original or valuable HDD.
- Never format, initialize, repair, repartition, mount, or write to source media.
- Recovery/acquisition output must be on separately verified physical storage.
- A passing CI run validates only the exact tested commit and does not prove native handle safety or physical HDD readiness.

---

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

## Disposable-VHDX coordinator integration — 2026-11-11

### New integration coverage

- [x] Expanded `WindowsVhdxSafetySmokeTests` from a policy-only test to include an internal coordinator acquisition test. The harness creates a new RAW VHDX, attaches it read-only, and confirms the Windows device is virtual/file-backed.
- [x] The public `PhysicalDiskImageAcquisition.AcquireAsync` boundary is still asserted to reject that virtual device before opening a source or creating output.
- [x] The second test reads the disposable VHDX through a read-only `PhysicalDriveN` handle, injects **synthetic test inventory/volume/extent metadata** into the internal coordinator, and uses the real Windows pinned-destination session for output.
- [x] The test verifies full image length, destination SHA-256 against the coordinator result, manifest format/length/hash and pre-publication verification marker, plus matching source SHA-256 before and after the acquisition.
- [x] It verifies image and manifest publication through the same pinned native destination session. The harness detaches the VHDX in its `finally` path and removes its temporary VHDX when detach is confirmed.
- [x] Full Windows CI run #1341 on coordinator-test commit `daf02e04f8a59363994e1fab20182f2f4176b5f8` passed: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43004485735. Build, normal tests, the opt-in VHDX policy + coordinator integration step, generated fixtures, CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and artifact uploads all succeeded.
- [x] Exact-head Windows CI #1342 completed successfully on script commit `4009d229c2ceb12710855e578f69472e41825a89`: normal suite **370 passed, 0 failed, 2 skipped (372 total)**; opt-in VHDX policy + coordinator integration **2 passed, 0 failed**; generated fixtures, CLI/recovery smoke tests, WPF build, Windows x64 desktop/CLI publishing, screenshot capture, and all artifact uploads succeeded. Run: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43004502496.

### Exact scope limits — do not overstate this result

- The coordinator test injects synthetic inventory and physical-volume/extent identities so that its internal preflight path can be exercised with a virtual device. It does **not** prove real Windows source-device identity for the VHDX, real source/destination physical-disk separation, or the production hardware identity gate.
- The destination folder is a temporary CI folder; this test is not a claim that its underlying physical storage is separate from the VHDX backing file.
- The source is opened read-only. The public acquisition API and UI remain hard-disabled; no real or valuable physical HDD has been opened or read.
- Existing deterministic coordinator tests still provide the failure-path coverage for cancellation, read errors, manifest commit failure, and path replacement. The new host integration test covers the successful acquisition/publication path; destination-full and full native cleanup/fault-injection scenarios remain separate acceptance work.
- The native publication write-through/directory-metadata durability question documented above remains open. Do not claim power-loss durability for the published name until a defensible contract is implemented and tested.
- Native ABI/layout assertions already exist for x86 and x64 layouts in the test source; current hosted Windows CI evidence is x64. A separate x86 runtime execution has not been demonstrated.

### Remaining gates before any physical HDD test

- [ ] Obtain final conclusion for exact latest-head CI #1342 and keep its artifact links/evidence.
- [ ] Add or complete native-session fault-injection coverage for destination-full, cleanup failure, and publication interruption as feasible without weakening safety.
- [ ] Resolve or explicitly constrain the publication durability contract and supported filesystems.
- [ ] Complete independent security review and any outstanding review comments.
- [ ] Confirm a dedicated spare, non-valuable physical HDD and a genuinely separate destination device are available for a supervised manual test. Never use the user's original or valuable HDD.
- [ ] Keep public physical acquisition disabled until a separate explicit release decision after all gates above.

**Current decision:** the disposable VHDX integration milestone has passed on CI #1341. The physical HDD test is **not authorized yet**. No date is promised; it can be scheduled only after the remaining gates are closed.

## Failure-path and durability review follow-up — 2026-11-11

### New deterministic coordinator fault-injection tests

- [x] Added a simulated destination flush failure test. It verifies the coordinator aborts before publication, removes the unpublished temporary image, creates no manifest, and disposes the source.
- [x] Added a simulated image-publication interruption test. It verifies the temporary image is removed, no final image or manifest appears, and the source is disposed.
- [x] Added a cleanup-failure test while cancellation is already unwinding. It verifies both the original cancellation failure and the secondary cleanup failure are retained in the resulting `AggregateException`, rather than allowing cleanup failure to mask the original error.
- [x] Existing tests continue to cover source-read failure, cancellation with successful cleanup, and manifest-publication failure after the verified image has committed.
- [ ] These new tests are fault-injected against the deterministic fake destination session. They do not reproduce a real filesystem ENOSPC condition, a physical device failure, or a Windows native-handle cleanup failure. Exact-head CI must pass before this test work is considered validated.

### Publication durability review decision

- [ ] The power-loss durability question remains open. `FlushFileBuffers`/stream flush on the image handle proves the image file's data flush path, but no supported, ordinary-user parent-directory metadata flush contract has been established for the rename.
- [ ] Do not simply enable `FILE_WRITE_THROUGH` on the original image handle without measuring/accepting the effect on the entire large sequential image write. Microsoft's documented NTFS metadata behavior is useful evidence, but it is not a blanket guarantee across every filesystem and does not by itself establish the exact contract for this adapter's pinned-handle rename.
- [ ] Any publication-only write-through approach must preserve the no-competing-writer/delete contract and must be validated against supported filesystems before implementation. Until then, describe the current behavior as file-content flush plus atomic no-replace publication, not as guaranteed power-loss-durable directory metadata.
- [ ] Explicitly constrain and document supported destination filesystems (currently local fixed drive-letter paths are accepted; this is not yet an NTFS-only or broader filesystem certification).

### Current safety decision

- Public physical acquisition remains hard-disabled and PR #30 remains draft/open.
- The disposable VHDX coordinator success path is validated on CI #1341/#1342, but synthetic inventory metadata means it does not prove production physical source identity or real physical destination separation.
- The new flush/publication/cleanup fault-injection tests await exact-head CI. A genuine full-disk test and native filesystem fault-injection remain separate work.
- No valuable or original HDD has been opened or read. A dedicated spare HDD test remains unauthorized until all security, durability, failure-path, and hardware-separation gates are closed.

## Exact-head validation update — 2026-11-11 (fault-injection suite and CI #1346)

This section supersedes the pending exact-head validation items in the preceding failure-path review. Older entries are retained as historical evidence.

### Candidate and CI

- [x] PR #30 remains **draft/open/unmerged** on `security/managed-destination-session-contract-20261110`.
- [x] Exact PR head before this checklist-only update: `736a8cee0b83fbd37bbff81959cc69c09adaf6e0`.
- [x] Full Windows CI #1346 passed on that exact implementation head: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43005495035.
- [x] Normal `DiskRescue.IO.Tests.dll` suite: **373 passed, 0 failed, 2 skipped (375 total)**.
- [x] Disposable VHDX safety/integration step: **2 passed, 0 failed**. The harness attached a newly generated RAW VHDX read-only as PhysicalDrive2; both the public-API virtual-device rejection test and the internal coordinator/native-destination integration test passed; the script completed its detach/cleanup path.
- [x] Remaining workflow stages passed: generated fixtures, CLI/recovery smoke tests, deleted-NTFS extraction and manifest checks, one-command lost-NTFS harness, WPF build, Windows x64 desktop and CLI publishing, dashboard screenshot capture, diagnostics upload, and all artifact uploads.
- [x] The three deterministic coordinator fault-injection regressions are included in the passing 373-test suite: destination flush failure cleanup, image-publication interruption cleanup, and preservation of both acquisition/cancellation and cleanup exceptions.
- [x] The PR API was rechecked: PR #30 is still open, draft, and unmerged. Public physical acquisition remains hard-disabled.

### Evidence limits and remaining gates

- [ ] A real disposable destination-full/ENOSPC test has **not** been run. The flush-failure test is deterministic fault injection against a fake session; it is not an ENOSPC simulation.
- [ ] Windows native-handle cleanup/publication fault injection remains outstanding; the three new tests do not inject failures inside the native adapter.
- [ ] Publication power-loss durability remains unresolved. Current evidence supports file-content flush followed by atomic no-replace publication; it does **not** establish power-loss durability of the renamed directory entry. Do not claim otherwise.
- [ ] Destination filesystem support still needs an explicit, tested compatibility contract. Current native adapter scope remains local fixed drive-letter paths and fail-closed reparse handling; broad filesystem certification is not established.
- [ ] Independent security review remains outstanding, including ABI/runtime architecture coverage, handle lifetime, reparse behavior, rename semantics, filesystem compatibility, and race assumptions.
- [ ] A dedicated spare, non-valuable physical HDD and a genuinely separate destination device have not been confirmed for supervised testing. No physical HDD test is authorized.
- [ ] Manual Windows keyboard/accessibility, high-DPI, and multi-work-area checks remain outstanding.

### Safety decision

The VHDX coordinator test uses synthetic inventory/volume/extent metadata and a temporary CI destination. It does not prove production physical-device identity or genuine physical source/destination separation. No original or valuable HDD has been opened or read. Keep the public acquisition API/UI disabled and PR #30 draft until the remaining gates are independently closed and a separate release decision is made.

## Engineering follow-up — 2026-11-11 (ENOSPC and publication durability investigation)

This is a design/investigation checkpoint only. No production behavior was changed in this update, and no new test result is claimed.

### Destination-full test design

- [x] Reviewed the native destination open path: destination files are created relative to the pinned directory handle with FILE_CREATE, GENERIC_READ | GENERIC_WRITE | DELETE, and restrictive ShareRead sharing. Native open failures currently surface as an IOException carrying the NTSTATUS value.
- [x] Confirmed the existing CI VHDX harness attaches a disposable RAW VHDX read-only for source testing; it does not create or format a destination volume. It cannot be repurposed as an ENOSPC test without adding a separately isolated destination-disk lifecycle.
- [ ] Build a dedicated, small, dynamically expanding test-only destination VHDX in the Windows runner. The harness must create a partition and filesystem only inside that newly generated disposable VHDX, assign a temporary drive letter, and verify the resolved destination volume is the expected test VHDX before writing anything.
- [ ] Constrain the test to a tiny destination capacity and write a generated in-memory source larger than the available free space. Assert that the acquisition fails before image publication, no final image or manifest appears, and all unpublished temporary files/handles are cleaned up.
- [ ] Ensure teardown detaches the destination VHDX before deleting its backing file; if detach cannot be confirmed, retain the backing file and fail the test rather than deleting an attached image.
- [ ] The test must never format, initialize, partition, or write to the source VHDX or any host/physical disk. The destination VHDX must be created by the test itself and identified independently before its one-time test-only formatting step.
- [ ] Capture the actual Windows error/status and filesystem free-space evidence. Do not treat a synthetic IOException, quota estimate, or fake-session flush exception as proof of ENOSPC.
- [ ] Before implementing this harness, inspect CI runner privileges and DiskPart/PowerShell teardown behavior; run it only in the hosted disposable Windows runner, not against user disks.

### Publication-only write-through candidate

- [x] Confirmed current native file creation uses FILE_SYNCHRONOUS_IO_NONALERT and does not request FILE_WRITE_THROUGH; the writer calls FileStream.Flush(flushToDisk: true) and then performs handle-relative no-replace rename through NtSetInformationFile.
- [x] Identified a candidate worth prototyping: close the ordinary sequential-write stream after its content flush, reopen the temporary leaf relative to the already-pinned directory with a publication-only FILE_WRITE_THROUGH handle, compare its handle-derived file identity with the identity captured before close, and perform the no-replace rename through that publication handle.
- [ ] This candidate is not yet proven safe. The close/reopen interval creates a name-race window; the implementation must fail closed if the reopened handle's volume/file ID differs, must keep the directory pinned, must retain no-replace semantics, and must prove no competing writer/delete can modify the file after it is reopened.
- [ ] Validate Windows share-mode behavior and file-ID comparison on supported NTFS versions before changing the adapter. A successful API call alone does not prove crash/power-loss durability.
- [ ] Measure the prototype on large generated sequential images to confirm write-through applies only to the publication handle and does not impose write-through latency on the full image-copy phase.
- [ ] Define the supported filesystem contract explicitly. Do not infer NTFS or ReFS durability guarantees from a generic local fixed-drive check; unsupported or unverified filesystems must remain outside the claimed contract.
- [ ] Add crash/power-interruption validation where feasible and document the limits of what automated tests can establish. Until then, retain the current wording: file-content flush followed by atomic no-replace publication; no guarantee of power-loss-durable directory metadata.

### Safety boundary

No code change or physical-media operation was made as part of this investigation. PR #30 remains draft/open, public physical acquisition remains hard-disabled, and physical HDD testing remains unauthorized. The only acceptable ENOSPC test target is a newly generated disposable destination VHDX on the hosted Windows CI runner.

### Windows harness feasibility review — 2026-11-11

- [x] Confirmed the current VHDX script already requires PowerShell 7 and Administrator privileges, and the CI workflow runs it on `windows-latest`. This provides a viable place to prototype a second, separate disposable destination VHDX without involving user hardware.
- [x] Confirmed the current script creates exactly one 128-MiB RAW VHDX and attaches it read-only. Its current test destination is an ordinary temporary folder on the runner volume; it is not a capacity-constrained destination.
- [x] Confirmed the current native destination adapter can open a drive-letter root directory as a pinned destination directory, provided Windows reports the volume as a local fixed drive and the path contains no rejected reparse-point component. This makes a temporary drive letter on a dedicated destination VHDX a plausible test route, subject to runtime verification.
- [ ] Prototype destination VHDX attachment and verify the exact attached disk by `Get-DiskImage -ImagePath` correlation before any DiskPart destructive command. Require one matching virtual disk, RAW state, and expected backing-file path; abort if any check is ambiguous.
- [ ] Select an unused drive letter programmatically, then format only the newly created destination VHDX. Re-query its disk/volume association and verify its volume GUID and drive letter before passing its root path to the native session.
- [ ] Keep the source VHDX read-only and preserve the current public API rejection test. The internal coordinator's synthetic inventory metadata remains test-only and must not be mistaken for production device identity validation.
- [ ] A 64-MiB destination versus the existing 128-MiB source is an initial capacity ratio to evaluate, not a guaranteed ENOSPC result; NTFS metadata, allocation and file growth behavior must be measured in the hosted runner. Assert actual free space and actual native write failure.
- [ ] Make cleanup conservative: detach both VHDXs independently, re-query attachment state, and delete each backing file only after Windows confirms it is detached. If either detach is uncertain, preserve that backing file and fail the job.

### ENOSPC coordinator seam — implementation refinement — 2026-11-11

- [x] Inspected the complete `WindowsVhdxSafetySmokeTests` coordinator harness and `PhysicalDiskAcquisitionCoordinator.RunPreflight`. The existing `VhdxCoordinatorTestEnvironment.AssessDestination` deliberately returns a synthetic separate-destination assessment (`CanUseAsSeparateDestination = true`) and maps its pinned destination session volume to synthetic disk 7. This already permits the internal coordinator to reach the real native destination writer without invoking the production free-space assessment.
- [x] Confirmed this is an existing internal test seam, not a production bypass. The public `PhysicalDiskImageAcquisition.AcquireAsync` hard-disabled policy test remains separate and must continue to pass.
- [x] Confirmed the real writer's image failure path calls `DeleteUnpublishedAsync` with `CancellationToken.None`; the native adapter marks the unpublished file for deletion using `SetFileInformationByHandle(FileDispositionInfo)`. Cleanup failure is surfaced together with the original failure as an `AggregateException`.
- [x] Confirmed the current native adapter creates the temporary image as a real `FileStream` on the pinned destination volume and writes image bytes through `DiskImager.WriteImageAsync`; therefore a genuinely undersized NTFS destination VHDX can exercise an operating-system write failure rather than a synthetic exception.
- [ ] Add an explicit destination-directory environment variable to the integration harness. Resolve it only to a newly created, uniquely named child directory on the verified destination VHDX; never let test cleanup target the volume root or a caller-supplied arbitrary directory.
- [ ] Extend the PowerShell harness to create and attach a second VHDX read/write. Before any initialization/formatting, correlate the exact backing image path through `Get-DiskImage -ImagePath`, require exactly one disk, verify its virtual bus, RAW style, expected capacity, and `IsBoot = false` / `IsSystem = false`; abort on any mismatch.
- [ ] Initialize/partition/format only that verified second VHDX as NTFS, assign a dynamically selected unused drive letter, and re-query the partition-to-disk and volume-to-letter associations before exposing its child directory to the test process. Never run formatting commands against the source VHDX or any pre-existing disk.
- [ ] Keep the source VHDX at 128 MiB and start with a 96-MiB destination VHDX; treat this as a sizing hypothesis. Record the destination's actual available bytes immediately before the copy and require it to be less than the source image length.
- [ ] Add a separate coordinator integration test that expects a real disk-full write exception (Windows `ERROR_DISK_FULL` / `ERROR_HANDLE_DISK_FULL`, or an explicitly verified equivalent), verifies no final image or manifest exists, verifies no temporary image remains, and hashes the read-only source before/after to prove it is unchanged. Do not mistake a preflight rejection, synthetic IOException, quota estimate, or manifest-only failure for ENOSPC.
- [ ] Detach source and destination VHDXs independently in `finally`; re-query each backing image's attachment state and delete each backing file only after Windows confirms that specific image is detached. If either detach is uncertain, preserve that image and fail the harness.
- [ ] Run the new harness only in the hosted Windows CI runner. The internal synthetic inventory does not validate production physical source/destination identity separation, and public physical acquisition remains hard-disabled.

### Publication verification race refinement — 2026-11-11

- [x] Identified that comparing only the reopened temporary file's volume/file ID would not prove its content remained unchanged across the close/reopen interval.
- [ ] Any publication-only reopen prototype must also verify the exact expected file length and recompute SHA-256 through the reopened handle before rename; the hash must match the digest produced by the original sequential copy.
- [ ] Retain restrictive sharing, pinned-directory-relative open, file identity comparison, and no-replace rename. Prove that a competing writer/delete cannot modify or replace the temporary file during or after reopen; fail closed on any mismatch.
- [ ] Do not implement the prototype until native NTSTATUS-to-Win32 error handling, handle lifetime, supported filesystem semantics, and write-through rename durability claims are independently reviewed and tested.

### Disposable NTFS ENOSPC harness — implementation and CI acceptance — 2026-11-11

- [x] Added `tools/Test-WindowsVhdxEnospc.ps1` as a separate hosted-Windows-only harness; retained the existing read-only policy smoke script unchanged.
- [x] Added a dedicated xUnit fact that runs only when both the source VHDX disk number and destination root are supplied by the ENOSPC harness. The ordinary VHDX policy harness therefore skips this test instead of accidentally running it without its destination.
- [x] Added a coordinator integration assertion using the existing synthetic inventory/destination assessment seam and the real native pinned destination adapter. The test creates its output in a unique child directory and refuses to accept the volume root as the output directory.
- [x] The script creates two distinct expandable VHDXs: a 128-MiB RAW source attached read-only and a 96-MiB destination attached writable. It correlates each disk to its exact `Get-DiskImage -ImagePath` backing file before proceeding; destination formatting is gated by exact image-path association, RAW state, writable state, and non-boot/non-system checks.
- [x] Initialized, partitioned, and formatted only the verified destination VHDX as NTFS; assigned a dynamically unused drive letter; re-queried the destination disk association and partition/volume mapping; verified a ready local fixed NTFS volume before opening the native destination session.
- [x] Hosted CI measured **85,762,048 bytes** available on the destination before acquisition, versus a **134,217,728-byte** source image. The test confirmed actual Windows disk-full error code 112 or 39 through the exception/HRESULT chain; this was not a preflight rejection or a synthetic exception.
- [x] The coordinator integration test passed: no final image or manifest was published, no temporary acquisition file remained after session cleanup, and the source SHA-256 before/after the failed acquisition matched.
- [x] Source and destination VHDXs were detached independently. CI logs confirm both detach operations succeeded; the script re-queried each image attachment state before deleting its backing file.
- [x] The normal VHDX policy smoke test passed separately, preserving the public hard-disabled acquisition boundary. The new ENOSPC fact is not enabled in that test and is run only by the dedicated two-VHDX script.
- [x] Full CI run **43029104611** passed on commit `75783a6d736a5161a5dd9f04ed14aa518ffd979d`: unit suite 373 passed / 0 failed / 3 skipped; ordinary VHDX policy integration passed; dedicated genuine ENOSPC integration passed; generated-fixture/CLI/recovery checks passed; WPF build, Windows x64 desktop and CLI publishing, and actual dashboard screenshot capture/upload passed.
- [ ] Repeat the dedicated ENOSPC run on another supported Windows runner / filesystem configuration before making broader filesystem compatibility claims. Current acceptance is for the hosted Windows runner's local NTFS destination.
- [ ] Independent review the new PowerShell harness and C# test for test-isolation, runner-version, and cleanup edge cases before considering the harness a release gate.

### ENOSPC safety scope after CI acceptance

This acceptance proves cleanup behavior when a **test-only internal coordinator** writes to a capacity-constrained disposable NTFS VHDX. The source is also a generated disposable VHDX and is opened read-only. The coordinator test uses synthetic inventory and destination-disk metadata; it does not validate production hardware identity reconciliation or physical source/destination separation. No user HDD or physical media was opened. Public physical acquisition remains hard-disabled and PR #30 remains draft/open/unmerged.

### Publication durability — Microsoft API contract review — 2026-11-11

- [x] Rechecked Microsoft's `NtCreateFile` documentation. Its `FILE_WRITE_THROUGH (0x00000002)` description says file-system services/drivers must transfer file data before a requested write operation is considered complete. It does not explicitly state that a subsequent `NtSetInformationFile(FileRenameInformation)` flushes rename metadata.
- [x] Rechecked Microsoft's Win32 `CreateFile` documentation. `FILE_FLAG_WRITE_THROUGH` is documented to cause NTFS to flush metadata changes, including rename operations, that result from processing the request. This is useful evidence for the Win32 flag, but is not by itself a documented equivalence guarantee for this adapter's `NtCreateFile + NtSetInformationFile` sequence.
- [x] Rechecked Microsoft's `FILE_RENAME_INFORMATION` contract: rename requires DELETE access; `ReplaceIfExists = FALSE` fails when the target already exists; `RootDirectory` supports a directory-handle-relative target; and special sharing/oplock rules apply to renaming open files.
- [x] Rechecked Microsoft's `CreateFile` sharing contract: share modes remain in effect until the handle closes; omitting FILE_SHARE_WRITE prevents new write opens and also causes a new open to fail when a writable file mapping exists. This supports a restrictive publication-handle design, but does not alone settle every filesystem/filter-driver race.
- [ ] Do not claim power-loss-durable rename metadata from the current implementation. Current evidence remains file-content flush followed by atomic no-replace handle-relative rename.
- [ ] Prototype a publication-only reopen after the sequential writer has flushed and closed its stream. Reopen the temporary leaf relative to the pinned directory with FILE_OPEN_REPARSE_POINT and FILE_WRITE_THROUGH, restrictive FILE_SHARE_READ, and only the read/delete rights needed for verification and rename.
- [ ] Capture the original volume/file ID and expected length/digest before closing. After reopen, compare volume/file ID, exact length, and recomputed SHA-256 before rename. Fail closed if the file is missing, substituted, modified, or cannot be reopened with the required share contract.
- [ ] Verify the reopened handle denies competing write/delete opens, including a pre-existing writable file mapping; verify no-replace rename remains handle-relative to the pinned directory. Add deterministic tests for replacement, content mutation, file-ID mismatch, competing writer/delete, and cleanup after failed verification.
- [ ] Benchmark the additional read/hash pass on large generated images. Do not add FILE_WRITE_THROUGH to the original sequential image-write handle.
- [ ] Run crash/power-interruption tests on disposable NTFS VHDX media and define the exact supported filesystem/Windows contract before changing the product's durability claim. API flag semantics and successful rename return alone are insufficient proof of power-loss durability.

### Repeated hosted-Windows validation after checklist and API-contract updates — 2026-11-11

- [x] Full CI run **43029862011** passed on current PR head `32422314071f3b35eddb351ade6231610814e25a`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43029862011
- [x] Build succeeded; unit suite: **373 passed, 0 failed, 3 skipped (376 total)**.
- [x] Existing disposable read-only VHDX policy test passed.
- [x] Dedicated two-VHDX NTFS ENOSPC test passed again. The real native coordinator encountered Windows disk-full; unpublished image cleanup succeeded; no final image/manifest remained; source hash was unchanged.
- [x] Generated-fixture CLI smoke tests, deleted-NTFS extraction/recovery-manifest smoke tests, and one-command lost-partition recovery harness passed.
- [x] WPF build and Windows x64 desktop/CLI publish succeeded. CI captured and uploaded a real WPF dashboard PNG (65,570 bytes) and the desktop/CLI preview artifacts.
- [x] CI uploaded test results and WPF startup diagnostics. Artifact listing: desktop preview, CLI preview, dashboard screenshot, test results, and startup diagnostics.
- [ ] Review the captured dashboard screenshot visually before marking the UI acceptance gate complete; CI verifies capture and PNG validity, not visual quality or operator usability.
- [ ] Physical HDD validation remains not authorized and not performed. Public physical acquisition remains hard-disabled; PR #30 remains draft/open/unmerged.

### Dashboard screenshot visual review — 2026-11-11

- [x] Downloaded and visually inspected the actual WPF dashboard screenshot artifact from successful CI run **43029862011** (65,570-byte PNG; generated with the synthetic valid MBR fixture).
- [x] Found a visible theme defect: the selected TreeView partition node rendered with a default light/system selection background, inconsistent with the dark navy workspace and cyan/teal selection language.
- [x] Added an explicit `TreeViewItem` style in `src/DiskRescue.Desktop/App.xaml` for dark-theme foreground/background, teal selected state, restrained hover state, padding, and keyboard focus visibility. Commit: `4cee1c483c24eab4af51b1fd0bdf6daf5d454ddd`.
- [ ] Re-review the next CI-captured dashboard screenshot to verify the selected node now uses the intended dark/teal style and no text contrast regression exists.
- [ ] The current screenshot's inspector uses a visible internal vertical scrollbar; CI screenshot alone does not establish usability at 100–200% scaling or constrained work areas. Keep those manual UI checks open.



### Dashboard selection-template correction — 2026-11-11

- [x] Reviewed the actual dashboard screenshot artifact from CI run 43030664353 after the first TreeView style correction.
- [x] Confirmed visually that the stock WPF TreeViewItem control template continued to paint the selected header with a light/system selection background; setting only the TreeViewItem Background/Foreground properties was insufficient.
- [x] Replaced the implicit TreeViewItem style with an explicit header/expander/items template in App.xaml. The template owns the selected, hover, and keyboard-focus visuals so the system selection brush cannot override the dark navy/teal palette.
- [ ] Await exact-head Windows CI and inspect its newly captured dashboard screenshot. Do not mark the selection styling fix complete until the selected node is visibly dark/teal and text contrast remains clear.
- [ ] The explicit template requires manual keyboard/tree-navigation, accessibility, and high-DPI verification; the template-level CI screenshot is not a substitute.
- [ ] Latest prior full validation remains CI run 43030664353 on 71094840c7f05edcf00e71ce0deaff82be3942e5 (373 passed, 0 failed, 3 skipped; VHDX policy and genuine NTFS ENOSPC tests passed; recovery smoke tests, WPF build/publish and screenshot capture passed). The new template commit is 4e7abcac2fc9bf4658620bd07517e09b08b925da; exact-head CI is pending.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested.


### Dashboard selection-template verification — 2026-11-11

- [x] Exact-head Windows CI completed successfully for the explicit TreeViewItem template and checklist commits (run 43031441909).
- [x] Downloaded and visually inspected the actual WPF dashboard screenshot artifact (65408-byte PNG). The selected partition header is dark teal with readable mint text; the stock light/system selection highlight is no longer visible.
- [x] Exact-head standard IO suite: 373 passed, 0 failed, 3 skipped (376 total).
- [x] Exact-head disposable VHDX policy/coordinator checks passed; genuine NTFS destination disk-full was reproduced with 85,762,048 free bytes measured before acquisition; unpublished partial image was removed, no final image/manifest was published, and read-only source hash remained unchanged.
- [x] Lost-partition discovery, bounded preview, and file extraction smoke test passed; WPF screenshot capture and desktop/CLI publish steps passed.
- [ ] Manual keyboard navigation, screen-reader/accessibility, high-DPI and real-user workflow checks remain outstanding; CI screenshot is visual evidence for the captured state only.
- [ ] Independent native adapter/security review and filesystem durability/compatibility evidence remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested.


### Destination-session disposal hardening — 2026-11-11

- [x] Source review confirmed a cleanup weakness in `WindowsPhysicalAcquisitionDestinationSession.DisposeAsync()`: a thrown exception from one owned stream/handle disposal stopped the loop and could leave later owned files and retained directory-chain handles undisposed.
- [x] Hardened disposal to attempt every owned stream, its associated handle, and every retained directory-chain handle in reverse order, even when an earlier close throws.
- [x] The session clears its ownership collections after all close attempts and reports disposal failures only after cleanup has been attempted: rethrows the single original exception with its stack, or reports multiple failures as an `AggregateException`.
- [ ] Add a deterministic fault-injection test proving later resources are still closed when an earlier resource's Dispose throws. Current native integration tests cover ordinary session disposal and release of the directory chain, but do not inject a failing Dispose implementation into this concrete adapter.
- [ ] Await exact-head Windows CI for the disposal hardening commit; do not treat the previous successful run as validation of this change.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested.

### Destination-session disposal fault-injection test — 2026-11-11

- [x] Extracted the production cleanup loop into the internal `DisposeResources` helper; `DisposeAsync()` uses this same helper for owned streams, associated handles, and reverse-order directory-chain handles.
- [x] Added deterministic fault injection with four disposable resources: the first and third throw distinct exceptions, while the second and fourth record successful disposal. The test asserts all four were attempted and both original exceptions were retained in order.
- [x] The test exercises the exact production cleanup helper without requiring a native Windows handle to throw during `Dispose()`.
- [ ] Await exact-head Windows CI for the source and test commits; this test is not accepted until that workflow completes successfully.
- [ ] The test validates continuation and failure collection in the shared cleanup routine; it does not simulate an operating-system SafeHandle close failure or prove hardware-level handle closure under kernel faults.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested.


### x86 Windows native-interoperability execution feasibility — 2026-11-11

- [x] Rechecked the existing CI workflow: it uses `windows-latest`, installs the default-architecture .NET 10 SDK, and runs the full solution test suite without an x86 runtime selection.
- [x] Rechecked the IO library and test project: both target `net10.0`; the current workflow does not install or explicitly invoke an x86 .NET host.
- [x] Confirmed from Microsoft's .NET 10 Windows support matrix that Windows 11 desktop supports x86, while the listed Windows Server releases support .NET 10 x64 only. The current workflow's hosted runner has not been established as a supported Windows desktop x86 environment.
- [x] Confirmed the VSTest documentation: `dotnet test --arch x86` selects the `win-x86` runtime identifier, but x86 test execution requires the x86 .NET runtime to be installed. The setup-dotnet action supports selecting an x86 SDK architecture; that alone does not prove the runner OS is a supported .NET 10 x86 platform.
- [ ] Establish an explicitly supported Windows 11 desktop x86 test environment (or another Microsoft-supported .NET 10 x86 environment) before adding an x86 CI gate.
- [ ] In that environment, install/use the x86 .NET 10 SDK/runtime, assert the test process is actually 32-bit (`IntPtr.Size == 4`), and run the IO native layout/handle tests. Preserve the existing x64 run as a separate required check.
- [ ] Do not claim x86 compatibility from source-level pointer-size assertions or from an x64 process merely targeting `win-x86`.
- [ ] No workflow or native source changes were made during this feasibility review. PR #30 remains draft/open/unmerged; public physical acquisition remains hard-disabled; no physical HDD was opened or tested.


### x86 feasibility documentation CI and dashboard artifact review — 2026-11-11

- [x] Exact-head CI run **43075184377** completed successfully for commit `a2ba87489c047ac08d4e4c0c2c4a6b7a978f149f`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43075184377
- [x] All **24/24** workflow steps succeeded, including build, test, disposable VHDX read-only policy, genuine NTFS ENOSPC cleanup integration, generated-fixture/CLI/recovery checks, WPF build, x64 desktop/CLI publish, screenshot capture, and artifact uploads.
- [x] Downloaded and parsed the exact-run TRX artifact: **377 passed, 0 failed, 3 not executed, 380 total; 377/377 executed tests passed**.
- [x] Downloaded and visually inspected the exact-run WPF screenshot artifact. The selected partition node remains dark teal with readable mint text; the earlier stock light/system selection defect has not returned.
- [ ] Screenshot review still shows the partition inspector content clipped within its fixed-height scrollable area; only part of the metadata is visible without scrolling. Review responsive sizing and ensure key fields remain discoverable at common DPI/scaling settings.
- [ ] Manual keyboard navigation, screen-reader/accessibility, high-DPI and constrained-work-area checks remain outstanding.
- [ ] x86 execution proof remains outstanding; this CI run is the existing Windows x64 path and must not be described as x86 validation.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested.


### Compact dashboard inspector refinement — exact-head validation — 2026-11-11

- [x] Reworked the right-hand inspector from a fixed-height partition-details textbox in a DockPanel into a four-row Grid: title, collapsible selected-partition details, inspection-report heading/warning, and a star-sized findings area.
- [x] The selected-partition details are now in a labeled Expander (collapsed by default), so the findings report receives the remaining panel height instead of losing a fixed 126 px to geometry text.
- [x] Exact-head Windows CI run **43079546402** passed on commit `24d664594f2f1acfafdd1ee32d2758f0710e8181`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43079546402
- [x] Downloaded and parsed the run's TRX artifact: **377 passed, 0 failed, 3 not executed (380 total); 377/377 executed tests passed**.
- [x] Downloaded and visually inspected the exact-head dashboard screenshot (979 × 623). The dark selected-partition state remains readable; the partition-details Expander is visible and the inspection warning/report heading remain in view. The findings textbox occupies the remaining lower inspector area.
- [ ] The screenshot is one synthetic-fixture state at one captured window size; it does not prove that long findings remain fully visible or usable at smaller windows, 125–200% DPI, or alternate display configurations. Continue manual responsive-layout checks.
- [ ] Verify keyboard focus/order and screen-reader announcements for the new Expander and findings region; visual screenshot review is not accessibility acceptance.
- [ ] x86 execution proof, independent native security review, and publication namespace/power-loss durability acceptance remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested; no physical HDD testing is authorized.


## Compact inspector follow-up — 2026-11-11

- Commit: `cfea0630809919e84fd38f4dfad272912e63d9bd` — `ui: preserve findings space in compact inspector`.
- Exact-head Windows CI run: [43084312993](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43084312993), completed successfully; 1/1 job successful.
- TRX result: 377 passed, 0 failed, 3 not executed, 380 total; all 377 executed tests passed.
- Captured dashboard screenshot reviewed at 979 × 623. Findings text now has a bounded minimum/maximum height inside a vertically scrollable inspector; partition details remain collapsible.
- Screenshot review notes remaining narrow-panel warning wrapping and no evidence yet for high-DPI, alternate display scaling, keyboard-only, or screen-reader acceptance.
- This is a visual/automated validation milestone only. It does not establish full responsive UI acceptance or production readiness.


### Compact inspector checklist commit CI — 2026-11-12

- [x] Exact branch-head commit `86a551c83fb2ac12fc212c7b6d9d03a57e1c6a1a` passed Windows CI run [43162451670](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43162451670); job `build-and-test` succeeded.
- [x] Downloaded and parsed the exact-run TRX artifact: **377 passed, 0 failed, 3 not executed, 380 total**; all 377 executed tests passed.
- [x] Branch head verified as `86a551c83fb2ac12fc212c7b6d9d03a57e1c6a1a` at validation time.
- [ ] This run is Windows x64 CI, not x86 runtime validation. The workflow installs the standard .NET SDK and runs the solution tests without `--arch x86`; published desktop and CLI artifacts target `win-x64` only.
- [ ] Manual high-DPI, constrained-work-area, keyboard-only, and screen-reader acceptance remain outstanding. No new manual display configuration was available from this CI artifact.
- [ ] Independent native security review and publication namespace/power-loss durability proof remain outstanding.
- [ ] PR #30 remains draft/open/unmerged; public physical acquisition remains hard-disabled. No physical HDD was opened or tested; physical HDD testing is not authorized.

### Physical-acquisition safety message and exact-head CI — 2026-11-12

- [x] Updated the desktop acquisition warning to accurately name the remaining gates: independent security review, broader Windows/filesystem compatibility testing, and disposable-VHDX end-to-end validation. No acquisition behavior or safety gate changed.
- [x] Exact-head Windows CI run [43163948097](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43163948097) passed on commit `3527d8541edd3643b8d71e9e6c8d2db0f3e689da`; all workflow steps completed successfully.
- [x] Downloaded and parsed the exact-run TRX artifact: **377 passed, 0 failed, 3 not executed, 380 total**; all 377 executed tests passed.
- [x] The ordinary test invocation skips the three opt-in VHDX facts by design. The workflow separately executes `Test-WindowsVhdxSafety.ps1` and `Test-WindowsVhdxEnospc.ps1`; both dedicated steps passed.
- [x] The read-only VHDX harness runs both the public hard-disabled policy test and `ReadOnlyVhdxSourceCompletesCoordinatorAcquisitionWithNativeDestinationSession`. The coordinator integration reads only a newly generated disposable VHDX through a read-only handle, uses synthetic inventory/volume/extent metadata, and publishes through the real pinned Windows destination session. It verifies image length/hash, manifest consistency and pre-publish verification, and source hash invariance.
- [x] The separate NTFS ENOSPC integration passed, including genuine disk-full handling and cleanup of unpublished output.
- [ ] Synthetic metadata does not prove production physical-device identity reconciliation or real physical source/destination separation. No original or valuable physical HDD was opened or read.
- [ ] Independent native security review, supported x86 runtime execution, broader filesystem/Windows compatibility, high-DPI/accessibility/manual workflow checks, and power-loss publication durability evidence remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled; physical HDD testing is not authorized.

## Latest parser validation — unterminated storage identity strings

- Parser rejects vendor/model/serial identity strings that have no NUL terminator within the declared descriptor size; unterminated strings are returned as null rather than consuming arbitrary trailing bytes.
- Regression fixture fills the complete declared string region with non-zero bytes, ensuring the test cannot pass due to zero-initialized fixture padding.
- Exact-head Windows CI passed on `120200b4d68709a2678adb6eb975c553cbf01477`: https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43168744497.
- Test results: 380 passed, 0 failed, 0 errors, 380 executed (383 total); both the unterminated-string regression and valid bounded-identity parser test passed.
- All 43 workflow steps passed, including disposable VHDX read-only policy and ENOSPC cleanup integration, generated fixtures, CLI/NTFS smoke tests, WPF build, x64 desktop/CLI preview publishing, and dashboard screenshot capture.
- This evidence validates parser behavior and the listed CI harnesses only. It does not authorize physical HDD acquisition or replace independent security review, supported-Windows compatibility testing, x86 runtime proof, power-loss durability validation, or manual UI/accessibility review.



### Disk metadata IOCTL response-length guard — exact-head validation — 2026-11-12

- [x] Hardened Windows disk geometry and disk capacity discovery: successful `DeviceIoControl` responses are now rejected unless `bytesReturned` contains the complete expected structure.
- [x] Added regression coverage for zero-byte and truncated responses, plus complete-response acceptance.
- [x] Implementation commit: `ed0fce83b4770f0fa71da4680cfd20aae94aa2bf`; regression-test commit / exact code head: `f9c8eee7efdec4faa7a5727eca9a4ffcd511dace`.
- [x] Exact-head Windows CI run [43173580462](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43173580462) completed successfully on `f9c8eee7efdec4faa7a5727eca9a4ffcd511dace`; all **24/24** workflow steps passed, including build, tests, disposable-VHDX read-only policy, disposable-VHDX ENOSPC cleanup, generated fixtures, CLI/recovery smoke tests, WPF build, x64 desktop/CLI preview publishing, screenshot capture, and artifact uploads.
- [x] Downloaded and parsed the exact-run TRX artifact: **384 passed, 0 failed, 0 errors, 384 executed; 3 additional tests marked NotExecuted (387 total)**. All 384 executed tests passed.
- [x] The three IOCTL response cases are covered by `DiscoveryRejectsIncompleteGeometryOrCapacityIoctlResponses` (zero-byte, truncated, and complete-sized case) and `DiscoveryAcceptsCompleteIoctlResponse`; the exact-head test suite passed.
- [ ] This validates response-buffer length handling in the automated Windows x64 test environment; it does not prove behavior across all Windows releases, storage drivers, filesystems, or physical hardware.
- [ ] Independent native security review, supported Windows x86 runtime execution, publication namespace/power-loss durability evidence, broader compatibility, and manual UI/high-DPI/accessibility checks remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested; physical HDD testing is not authorized.


### Volume-map fail-closed unmapped-disk conclusion — exact-head validation — 2026-11-12

- [x] Tightened `PhysicalVolumeMappingResult.CanTreatDiskAsUnmapped`: a disk is treated as unmapped only when enumeration is complete, the disk has no volume-path entry, and the result contains no diagnostic issues.
- [x] Added regression coverage proving that a nominally complete result with a volume-extent diagnostic cannot be used to conclude that a disk is unmapped; retained the clean-complete-map positive case.
- [x] Implementation commit: `03aed431b8a194960c1495bc73c8ed3cf6183cbd`; regression-test commit / exact PR head: `93c41e5b97b265ffb86b30bc57058eb37df7f586`.
- [x] Exact-head Windows CI run [43182609620](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43182609620) completed successfully on `93c41e5b97b265ffb86b30bc57058eb37df7f586`; all **24/24** workflow steps passed, including build, test invocation, disposable-VHDX read-only policy and ENOSPC integration, generated fixtures, CLI/NTFS smoke tests, WPF build, x64 desktop/CLI preview publishing, screenshot capture, and artifact uploads.
- [x] Downloaded and parsed the exact-run TRX artifact: **385 passed, 0 failed, 0 errors; 388 test cases listed, 385 executed and 3 marked NotExecuted**. The three skipped cases are opt-in VHDX integration facts; the workflow separately ran the dedicated VHDX read-only policy and ENOSPC integration steps successfully.
- [ ] This validates the helper's fail-closed behavior in the automated Windows x64 environment only; it does not prove physical disk inventory correctness, all storage-driver behavior, or compatibility across Windows/filesystem versions.
- [ ] Independent native security review, supported Windows x86 runtime execution, publication namespace/power-loss durability evidence, broader compatibility, and manual UI/high-DPI/accessibility checks remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested; physical HDD testing is not authorized.

### Boot/volume metadata malformed-input regression coverage — exact-head validation — 2026-11-12

- [x] Added five regression cases for truncated MBR partition fields, excessive volume-extent count, negative extent start, non-positive extent length, and overflowing extent end.
- [x] Regression-test commit / exact code head: `d1625db0ae6c52e7d11c285adc90a5cd4e6aa491`.
- [x] Exact-head Windows CI run [43199347292](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43199347292) completed successfully on the exact code head; all **24/24 workflow steps** passed, including build, test invocation, disposable-VHDX read-only policy and ENOSPC cleanup integration, generated fixtures, CLI/NTFS recovery smoke tests, WPF build, Windows x64 desktop/CLI preview publishing, dashboard screenshot capture, and artifact uploads.
- [x] Downloaded and parsed the exact-run TRX artifact: **390 passed, 0 failed, 0 errors; 393 test results listed, 390 executed and 3 marked NotExecuted**. The three skipped cases are opt-in VHDX integration facts. The workflow separately ran the dedicated read-only VHDX policy and ENOSPC cleanup integration steps successfully.
- [x] The five new malformed-metadata regression cases were included in the successful exact-head test run.
- [ ] This validates parser rejection behavior in the automated Windows x64 environment; it does not establish complete boot-chain attestation, physical-device identity correctness, or compatibility across all Windows/storage-driver/filesystem combinations.
- [ ] Independent native security review, supported Windows x86 runtime execution, publication namespace/power-loss durability evidence, broader compatibility, and manual UI/high-DPI/accessibility checks remain outstanding.
- [ ] PR #30 remains draft/open/unmerged. Public physical acquisition remains hard-disabled. No physical HDD was opened or tested; physical HDD testing is not authorized.

## Publication write-through scope correction — 2026-11-12

- [x] Rejected the first write-through prototype that set `FILE_WRITE_THROUGH` in the initial `NtCreateFile` options. That would have applied write-through behavior throughout the large sequential image-copy phase and contradicted the publication-only performance constraint.
- [x] Prototyped a publication-stage mode transition on the existing file handle using `NtSetInformationFile(FileModeInformation)`. The native call returned success, but the subsequent `NtQueryInformationFile` read-back did **not** show `FILE_WRITE_THROUGH` enabled on the Windows CI runner. The candidate is therefore rejected; a success status alone was not accepted as proof.
- [x] Removed the ineffective mode transition and kept `FILE_WRITE_THROUGH` out of the initial `NtCreateFile` options. This avoids imposing write-through on the large sequential image-copy phase.
- [x] Updated the Windows-native regression assertion to verify the handle remains non-write-through both after creation and after the current publication path. This guards against accidentally reintroducing the full-copy performance penalty.
- [x] Failed candidate evidence: Windows CI [43217951212](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43217951212) compiled successfully but failed the read-back assertion (389 passed, 1 failed, 3 NotExecuted). The failed assertion demonstrated that the mode flag was not retained.
- [x] Reverted safe implementation and non-write-through regression assertion passed exact-head Windows CI [43219758725](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43219758725) on `16c9beb87018ed09e515e26ebf65bb50e8194e58`; all **24/24** workflow steps passed. The TRX artifact contained **392 passed, 0 failed, 4 NotExecuted (396 listed)**; both new publication-flush failure-path tests passed. The workflow separately ran the disposable NTFS VHDX volume-flush integration test successfully.
- [x] The publication-only alternative is now implemented as a volume-handle flush; see the next section for exact-head evidence and remaining durability limits.
- [ ] Perform controlled crash/recovery validation only on newly generated disposable NTFS VHDX media. A guest/VM crash test must be described as filesystem crash-recovery evidence only; it does not prove host power-loss behavior or that physical storage hardware honors flush requests.
- [ ] Do not claim power-loss durability, filesystem-wide compatibility, or hardware-cache guarantees until the exact contract and limits are independently established.

**Safety boundary:** no physical HDD access or testing. Source remains read-only; all validation uses generated disk images and disposable VHDX media. Public physical acquisition remains hard-disabled; PR #30 remains draft/open/unmerged.
## Volume-handle publication flush — implementation and exact-head validation — 2026-11-12

- [x] Added `FlushPublishedNamespaceAsync` to the destination-session contract. The Windows adapter resolves the volume-GUID path from the already-pinned destination directory handle, opens that volume handle with write and read-attributes access, and calls `FlushFileBuffers`. Opening the volume handle for write access requires administrative privileges; this does not open a physical-disk handle.
- [x] Before flushing, the adapter queries the volume serial through both the pinned directory handle and the opened volume handle with `GetVolumeInformationByHandleW`; it fails closed if the serials differ. This prevents flushing a volume handle that does not correspond to the pinned destination directory.
- [x] The writer requests the volume flush after the image rename and again after the manifest rename. The large sequential image-copy handle remains non-write-through.
- [x] Added `PhysicalAcquisitionPublicationDurabilityException`: if a volume flush fails after a successful rename, the published entry is preserved and the caller is explicitly told that namespace durability is unconfirmed. The writer does not attempt unpublished-file cleanup on an already-published entry.
- [x] Added fake-session regression tests for both failure points: image flush failure preserves the published image; manifest flush failure preserves both published entries. Both tests passed in exact-head CI.
- [x] Added and executed `PinnedVolumeHandleFlushesPublishedEntryOnDisposableNtfsVhdx` through `tools/Test-WindowsVhdxEnospc.ps1`. The test creates a file on the script-created disposable NTFS destination VHDX, flushes the file, publishes by the pinned handle-relative no-replace path, verifies the volume-handle identity, calls the new volume-flush contract, closes the session, and verifies the published bytes.
- [x] The disposable VHDX integration step passed in exact-head CI; the log confirms: `FlushFileBuffers completed through the pinned volume-GUID handle on the disposable NTFS destination VHDX.` The same step also passed the existing real ENOSPC cleanup test.
- [x] Final code/test head `ed18de1a5290445df05e063de39d55b53c534392`; Windows CI [43220489979](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/43220489979) completed successfully with **24/24 workflow steps passed**. TRX: **392 passed, 0 failed, 4 NotExecuted**; both publication-flush failure-path tests passed, and the opt-in VHDX volume-flush test was separately executed and passed in the dedicated integration step.
- [ ] Independently confirm that the documented volume-handle flush contract covers the parent-directory rename metadata for the supported NTFS versions and the exact handle-relative rename sequence. Microsoft documents flushing all open files on a volume; this evidence does not itself establish every filesystem's namespace crash-consistency contract.
- [ ] Measure the cost of flushing all open files on the destination volume after both publications. The operation may flush unrelated open files on that volume; production performance and operational impact are not yet benchmarked.
- [ ] Perform controlled crash/recovery validation only on generated disposable NTFS VHDX media. A guest/VM crash test is filesystem crash-recovery evidence only; it does not prove host power-loss behavior.
- [ ] Do not claim survival through every sudden-power-loss scenario or hardware-cache behavior. Storage devices that fail to honor flush requests remain outside this software guarantee.

**Safety boundary:** no physical HDD access or testing. Source remains read-only; all validation uses generated disk images and disposable VHDX media. Public physical acquisition remains hard-disabled; PR #30 remains draft/open/unmerged.