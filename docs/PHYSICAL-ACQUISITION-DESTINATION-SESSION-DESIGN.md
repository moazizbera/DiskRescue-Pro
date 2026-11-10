# Physical Acquisition Destination Session — Design Gate

**Status:** design proposal; not an implementation or security certification  
**Related:** [Issue #23](https://github.com/moazizbera/DiskRescue-Pro/issues/23)  
**Base reviewed:** `main` after PR #28 (2026-11-10)

## 1. Problem statement

The current physical acquisition coordinator performs repeated path-based destination preflight, then delegates image creation to `DiskImager.CreateImageAsync`. That writer creates a sibling temporary file by path, verifies by reopening a path, and publishes by path-based move. The manifest publisher independently creates and publishes its temporary file by path.

A final path-based preflight narrows the race window but does not bind later filesystem operations to the directory object and volume that were assessed. Another path check is not an adequate fix.

## 2. Required ownership model

Introduce an acquisition-scoped destination session, created after source selection but **before the first source read**, and retained until both image and manifest publication finish.

Proposed abstraction (names are illustrative; finalize against project conventions):

- `IPhysicalAcquisitionDestinationSession : IAsyncDisposable`
  - immutable verified destination volume identity;
  - immutable verified directory identity (volume serial + file ID, with documented filesystem limitations);
  - methods to create-new image and manifest temporary files relative to the pinned directory;
  - methods to verify, flush, and publish already-open files with no-replace semantics;
  - no method accepting an arbitrary destination directory path after session creation.
- `IPhysicalDiskAcquisitionEnvironment.OpenDestinationSession(...)`
  - Windows production implementation only for physical acquisition;
  - test implementation may use deterministic in-memory handles/identities;
  - existing non-physical image APIs remain unchanged.

The session owns its directory handle and every outstanding temporary-file handle. The coordinator owns the session lifetime. Disposal must occur on every success, cancellation, and exception path.

## 3. Windows implementation requirements

1. Open the destination directory with a directory-capable handle and query its final resolved path, volume identity, and directory file ID. Do not infer identity from the input string alone.
2. Inspect reparse-point semantics. Do not blanket-reject all reparse points: legitimate volume mount points must be resolved and validated. Reject symlinks/junctions when the final target cannot be safely pinned and classified.
3. Create sibling temporary files relative to the pinned directory handle using native handle-relative creation and create-new semantics. Candidate native mechanism: `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory`; native declarations, NTSTATUS mapping, access masks, sharing, and cleanup require code review.
4. Query the created file's volume identity and prove it matches the session before any source byte is read.
5. Stream source bytes into the already-open temporary image handle. Hash and verify through that same handle; flush file contents and metadata as required before publication. Do not close and reopen by path for verification.
6. Publish by handle-relative rename with no-replace semantics, retaining the pinned root directory. Candidate mechanism: `SetFileInformationByHandle` with the appropriate rename information class and root-directory handle. Confirm supported Windows versions/filesystems; no path-based fallback.
7. Create and publish the manifest through the same session and volume. If manifest publication fails after image commit, preserve the committed image and surface an explicit manifest-publication failure; do not delete or overwrite the image as a compensating action.
8. If any required native operation, identity query, filesystem capability, or no-replace guarantee cannot be established, abort before the first source read. Do not fall back to current path-based image/manifest methods.

## 4. Coordinator ordering

Required high-level order:

1. Validate selected source and initial destination request.
2. Run source, boot-device, volume-map, and destination policy preflight.
3. Open the source read-only and revalidate source identity.
4. Open destination session; verify directory and volume identities and destination separation.
5. Perform a final inventory/boot-map/destination revalidation and compare it with the pinned session identity.
6. Create the image temporary file relative to the pinned session; verify its volume identity.
7. Only after steps 1–6 pass, permit the first source read.
8. Copy, hash, flush, and verify through the same image handle.
9. Publish image no-replace through the pinned session.
10. Publish manifest no-replace through the same session.
11. Dispose all file and directory handles.

Any failure through step 6 must produce **zero source reads**, no published image/manifest, and deterministic handle disposal.

## 5. Required tests

Windows CI tests must use temporary directories, generated bytes, and disposable VHDX only—never a physical source HDD.

- Ordinary directory: session opens; volume and directory identity captured.
- Directory path replacement after session open: operations remain bound to original directory handle or fail; they never redirect to replacement path.
- Junction and symbolic link: explicit policy result and fail-closed behavior when target pinning is unsupported.
- Volume mount point: accepted only when resolved target volume identity is proven and stable; privilege/environment-dependent test may be skipped with a precise reason.
- Created temp file: same volume identity as pinned directory; mismatches fail before any source read.
- Existing final image/manifest: no-replace behavior; existing bytes unchanged.
- Image verification: same open handle used for writing, hashing, and verification; no path reopen.
- Manifest publication: same pinned directory and volume; no path re-resolution.
- Failure injection at each native boundary: zero source reads before the read gate; all handles disposed; no published artifacts.
- Cancellation and exceptions: handles disposed; committed image preserved if manifest commit fails.
- Unsupported filesystem/native capability: explicit fail-closed result; prove no path-based fallback is invoked.

## 6. Testability and implementation constraints

- Keep native P/Invoke declarations isolated behind a small Windows-specific adapter.
- Make native calls injectable so tests can force identity mismatch, rename failure, unsupported information classes, and path replacement deterministically.
- Record native error codes and operation names in safe diagnostics; do not log sensitive serials or raw device contents.
- Verify file ID/volume identity semantics on supported filesystems; do not assume file IDs are globally unique or stable across copies.
- Review handle access rights, share modes, backup semantics, reparse flags, rename flags, NTSTATUS conversion, cancellation, and disposal for leaks/races.
- Do not introduce a second Python/runtime dependency or external native package for this feature.

## 7. Acceptance gates

- [ ] Design reviewed against target Windows versions and supported filesystems.
- [ ] Directory session and native adapter implemented behind physical-acquisition-only boundary.
- [ ] Image temp create, write, hash, verify, flush, and no-replace rename are handle-bound.
- [ ] Manifest temp create and no-replace publication use the same session.
- [ ] Reparse semantics are explicit; legitimate mount points are not rejected indiscriminately.
- [ ] All deterministic Windows tests pass, with explicit skips for unavailable mount-point privileges.
- [ ] Exact-head Windows CI passes.
- [ ] Post-merge mainline CI passes.
- [ ] Manual Windows tests on disposable VHDX pass and evidence is recorded.
- [ ] Independent security review accepts the implementation and residual limitations.

## 8. Safety boundary

This design does not authorize physical partition scanning or recovery, and it does not certify hardware acquisition. Physical acquisition remains gated until the implementation, Windows tests, and review satisfy the acceptance gates. Do not test with a valuable or original HDD. A hardware write blocker remains recommended for irreplaceable or evidentiary media.

## 9. Verified implementation seams on current main

The source review was repeated after PR #28 merged. The following is a direct description of the current code, not an assumption about future behavior:

- `DiskImager.CreateImageAsync` calls `Path.GetFullPath`, derives a sibling `.partial` pathname, opens it with `FileStream(FileMode.CreateNew)`, closes it, verifies it by calling `DiskImageVerifier.VerifyAsync(tempPath, ...)`, and publishes it with `File.Move(tempPath, fullPath, overwrite: false)`.
- `PhysicalDiskManifestPublisher.PublishAsync` separately normalizes the manifest path, creates a temporary manifest by pathname, closes it, and publishes it with `File.Move(..., overwrite: false)`.
- `PhysicalDiskAcquisitionCoordinator` currently invokes path-based destination assessment, the path-based image writer, and the path-based manifest publisher.
- The existing no-overwrite flags prevent ordinary name collisions; they do not pin the directory object or volume across creation, verification/reopen, and rename.
- PR #28 closes public raw physical-reader and coordinator API bypasses. It does not change the path-based writer lifecycle described above.

Implementation must therefore add a distinct physical-acquisition-only session API and route every physical-acquisition image/manifest operation through it. Do not retrofit or silently change the generic image-file writer unless its separate callers and compatibility are explicitly covered by tests.

## 10. Required implementation sequence (no speculative native interop)

1. Add the managed session contract and deterministic fake session first. Add coordinator tests proving the session is opened and the image temp is created before the source read gate; every session failure must assert zero source reads and disposal.
2. Refactor the physical coordinator behind the session contract while retaining the hard-disabled public API. The generic image-file workflows must remain on their existing API.
3. Implement a Windows-only native adapter in isolation. Start with directory handle acquisition, final-path/volume/file-ID queries, and explicit reparse-tag classification; validate each operation on supported Windows CI before adding write operations.
4. Implement handle-relative create-new and no-replace rename in a focused adapter test harness using temporary directories. Validate exact native structures, alignment, access masks, NTSTATUS conversion, and handle ownership.
5. Add same-handle stream/hash/verification and manifest operations. Remove every path reopen and path-based fallback from the physical acquisition call graph.
6. Run the complete deterministic Windows race/reparse/path-replacement test matrix, then exact-head and post-merge CI. Keep the public acquisition gate disabled pending manual disposable-media acceptance and independent review.

If a native primitive cannot be proven on the supported Windows/filesystem matrix, stop and preserve the hard gate. Do not substitute a second path preflight, path normalization, or a best-effort fallback.
