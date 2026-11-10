# Physical Acquisition Destination Session — Native API Implementation Notes

**Status:** implementation research; not a native implementation or certification  
**Related:** [Issue #23](https://github.com/moazizbera/DiskRescue-Pro/issues/23), [PR #30](https://github.com/moazizbera/DiskRescue-Pro/pull/30)  
**Reviewed:** 2026-11-10

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

- `SetFileInformationByHandle` accepts `FileRenameInfo` and `FILE_RENAME_INFO`.
- `FILE_RENAME_INFO.RootDirectory` may be a directory handle when the target name is relative.
- `ReplaceIfExists = FALSE` means an existing target causes the rename to fail rather than being replaced.
- The native buffer layout must be constructed with correct x86/x64 alignment and byte length; do not assume a managed sequential struct with an inline one-character filename is automatically safe.
- The adapter must retain the root directory handle until image and manifest publication are complete.

References:
- https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
- https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_rename_info

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
3. Inspect every relevant reparse component, recording tag and resolved target. Preserve only explicitly supported and validated volume mount points.
4. Reconcile the pinned directory's volume identity with the existing physical volume map and reject a source/destination physical-disk overlap.
5. Create a unique temporary leaf using `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory` set to the pinned handle and `FILE_CREATE` disposition.
6. Query `FILE_ID_INFO` from the created file handle and compare its volume serial with the pinned directory before permitting any source read.
7. Stream, hash, and verify through the same open file handle; call `FlushFileBuffers` before publication.
8. Publish with `SetFileInformationByHandle(FileRenameInfo)`, relative `RootDirectory`, and `ReplaceIfExists = FALSE`.
9. Repeat the same session-bound create/serialize/flush/publish sequence for the manifest.
10. Keep directory and file handles alive until both commits finish; dispose all remaining temporary handles on every failure path.

## 3. Required native tests before coordinator integration

- x64 layout/offset tests for `OBJECT_ATTRIBUTES`, `UNICODE_STRING`, `IO_STATUS_BLOCK`, `FILE_ID_INFO`, and the variable-length rename buffer.
- Normal directory: open, identity capture, relative create, identity match, write, flush, rename.
- Existing temporary/final name: create/rename fails without modifying the existing entry.
- Directory renamed or original path replaced after pinning: all operations remain attached to the pinned directory object; no replacement-path output appears.
- Symlink/junction and volume mount point: tag and target classification is explicit; only verified mount points are accepted.
- Destination file volume mismatch or missing file ID: fail before the first source read.
- Inject failure at every native boundary and assert zero source reads before the read gate, no unexpected published files, and deterministic handle disposal.
- Manifest failure after image publication preserves the image and reports its name/hash.
- Test only temporary directories, generated content, and disposable VHDX. Never use an original or valuable physical HDD.

## 4. Explicit non-goals and gate

This document does not authorize coordinator integration with a path-based fallback, physical acquisition, physical partition scanning, or recovery. The native adapter must be isolated, injectable, reviewed, and tested on the supported Windows/filesystem matrix before the production coordinator can use it. If any required primitive or filesystem identity cannot be proven, keep the public acquisition gate disabled.

The managed session writer in PR #30 is only the orchestration layer. It does not implement these native primitives and does not close the path-replacement/reparse race by itself.
