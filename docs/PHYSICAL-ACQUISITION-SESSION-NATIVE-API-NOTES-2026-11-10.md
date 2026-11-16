# Physical Acquisition Destination Session — Native API Implementation Notes

**Status:** first native adapter and internal coordinator integration implemented; not a security certification  
**Related:** [Issue #23](https://github.com/moazizbera/DiskRescue-Pro/issues/23), [PR #30](https://github.com/moazizbera/DiskRescue-Pro/pull/30)  
**Reviewed:** 2026-11-10

## Current implementation status — 2026-11-10

The first native adapter is now connected to the internal acquisition coordinator on PR #30.

- The directory session exposes the volume-GUID root resolved from the pinned handle using `GetFinalPathNameByHandleW(VOLUME_NAME_GUID)`.
- It also resolves the current directory name from the same handle using `GetFinalPathNameByHandleW(VOLUME_NAME_DOS)`. After a directory rename, the result path and manifest do not blindly reuse the original caller-supplied path.
- The native test moves the opened directory, creates a replacement at the old textual path, verifies the pinned handle reports the moved directory, and verifies relative file creation still lands in the moved directory.
- The coordinator opens the source read-only, performs final pre-read preflight, opens the pinned destination session, maps the handle-derived volume GUID through the complete Windows volume map, compares mapped disk numbers with destination extent numbers from preflight, and validates source/destination physical-disk identities before the first source read.
- The coordinator uses only the session writer for image and manifest create/verify/flush/no-replace publication. It no longer calls the path-based image or manifest writers.
- The manifest records the handle-derived volume GUID, directory file ID, and directory path resolved from the pinned handle at manifest creation; returned image and manifest paths are resolved from the pinned handle after publication.

This is an internal, gated implementation. The public physical-acquisition entry point remains hard-disabled. Passing CI on one Windows runner is not broad compatibility certification.


## 1. Confirmed Windows API semantics

The following Microsoft documentation supports the candidate primitives. These references establish individual API semantics only; they do not prove that the complete acquisition protocol is race-free.

### Directory-relative create-new

- User-mode `NtCreateFile` is documented in `winternl.h`.
- `OBJECT_ATTRIBUTES.RootDirectory` can contain a previously opened directory handle; when non-null, `ObjectName` is resolved relative to that directory.
- `FILE_CREATE` creates only when the entry does not exist and returns an error if it already exists.
- The adapter must use a single validated leaf name, not accept a path after the directory session has been opened.
- `NtCreateFile` returns NTSTATUS; native status handling must preserve the operation and status in diagnostics.

Reference: https://learn.microsoft.com/windows/win32/api/winternl/nf-winternl-ntcreatefile

### Handle-relative no-replace rename

- The Win32 documentation says `FILE_RENAME_INFO.RootDirectory` may be a directory handle for a relative target name and `ReplaceIfExists = FALSE` prevents replacement.
- The first implementation used `SetFileInformationByHandle(FileRenameInfo)`; the Windows CI runner returned `ERROR_INVALID_PARAMETER (87)` for a non-null `RootDirectory`, despite a correctly sized x64 buffer. This is a reproduced implementation failure, not a reason to fall back to a path-based rename.
- The adapter now uses the native `NtSetInformationFile` entry point with `FileRenameInformation` (information class 10), a directory-relative `RootDirectory`, and `ReplaceIfExists = FALSE`. It handles `STATUS_OBJECT_NAME_COLLISION` as a no-replace conflict.
- This native call is an implementation dependency that requires direct Windows tests across the supported filesystem/Windows matrix. Do not treat the API note or a single CI runner as broad compatibility certification.
- The variable-length buffer uses the documented union/handle/length/name offsets; x86 still requires explicit validation before x86 support can be claimed.
- The adapter retains the root directory handle until image and manifest publication are complete.

References:
- https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
- https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_rename_info
- https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information

### Directory and file identity

- `GetFileInformationByHandleEx(FileIdInfo)` returns `FILE_ID_INFO`.
- `FILE_ID_INFO` contains a volume serial number and a 128-bit file identifier. Microsoft documents their combination as identifying a file on one computer and recommends comparing both values when determining whether two handles refer to the same file.
- A volume serial is not a globally unique physical-device identifier. The adapter must also reconcile the opened destination handle's volume identity with the existing physical-volume-to-disk mapping used by the acquisition policy.
- If the filesystem cannot provide a reliable identity, fail closed before source reads.

References:
- https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_info
- https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex

### Reparse-point classification

- Reparse-point presence can be detected through file attributes; opening with `FILE_FLAG_OPEN_REPARSE_POINT` allows inspection of the reparse point itself instead of transparently following it.
- `FSCTL_GET_REPARSE_POINT` retrieves the reparse data, including its tag.
- The implementation must classify the tag and target. It must not treat all reparse points as equivalent: volume mount points and symbolic links/junctions have different safety implications.
- A mount point is acceptable only if the resolved target volume is the exact volume pinned by the session and is reconciled with the physical-disk mapping. Unresolved or unsupported redirection must fail closed.

References:
- https://learn.microsoft.com/windows/win32/fileio/reparse-point-operations
- https://learn.microsoft.com/windows-hardware/drivers/ifs/fsctl-get-reparse-point

## 2. Adapter sequence to implement and test

1. Open the requested destination directory with directory-capable handle semantics; retain the handle.
2. Capture final resolved path and `FILE_ID_INFO` from the open handle.
3. Open each path component relative to the already-pinned parent with `FILE_OPEN_REPARSE_POINT`; inspect the opened component's attributes and reject every reparse point in this first implementation. Volume mount points, symlinks, and junctions are currently unsupported; no transparent redirection is followed.
4. Reconcile the pinned directory's volume identity with the existing physical volume map and reject a source/destination physical-disk overlap.
5. Create a unique temporary leaf using `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory` set to the pinned handle and `FILE_CREATE` disposition.
6. Query `FILE_ID_INFO` from the created file handle and compare its volume serial with the pinned directory before permitting any source read.
7. Stream, hash, and verify through the same open file handle; call `FlushFileBuffers` before publication.
8. Publish with `NtSetInformationFile(FileRenameInformation)`, relative `RootDirectory`, and `ReplaceIfExists = FALSE`; preserve native NTSTATUS for diagnostics.
9. Repeat the same session-bound create/serialize/flush/publish sequence for the manifest.
10. Keep directory and file handles alive until both commits finish; dispose all remaining temporary handles on every failure path.

## 3. Required native tests before coordinator integration

- x64 layout/offset tests for `OBJECT_ATTRIBUTES`, `UNICODE_STRING`, `IO_STATUS_BLOCK`, `FILE_ID_INFO`, and the variable-length rename buffer.
- Normal directory: open, identity capture, relative create, identity match, write, flush, rename.
- Existing temporary/final name: create/rename fails without modifying the existing entry.
- Directory renamed or original path replaced after pinning: all operations remain attached to the pinned directory object; no replacement-path output appears.
- Symlink/junction and volume mount point: each reparse component is opened without following and rejected; any future mount-point support needs separate target-volume/physical-map validation.
- Destination file volume mismatch or missing file ID: fail before the first source read.
- Inject failure at every native boundary and assert zero source reads before the read gate, no unexpected published files, and deterministic handle disposal.
- Manifest failure after image publication preserves the image and reports its name/hash.
- Test only temporary directories, generated content, and disposable VHDX. Never use an original or valuable physical HDD.

## 4. Explicit non-goals and gate

This document does not authorize coordinator integration with a path-based fallback, physical acquisition, physical partition scanning, or recovery. The current native adapter is a first implementation slice only: it is limited to local fixed drive-letter paths, rejects all reparse points, and is not wired into the coordinator. It must be reviewed and tested on the supported Windows/filesystem matrix before production use. If any required primitive or filesystem identity cannot be proven, keep the public acquisition gate disabled.

The managed session writer in PR #30 is only the orchestration layer. It does not implement these native primitives and does not close the path-replacement/reparse race by itself.
