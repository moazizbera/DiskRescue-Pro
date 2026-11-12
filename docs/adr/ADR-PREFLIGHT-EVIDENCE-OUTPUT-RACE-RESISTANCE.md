# ADR: Race-Resistant Preflight Evidence Output

- Status: Accepted as a security requirement; implementation deferred
- Date: 2026-11-12
- Scope: `tools/Invoke-HyperVCrashPreflight.ps1`
- Safety classification: Host-side evidence output only; no source VHDX writes

## Context

The Hyper-V preflight adapter writes a JSON evidence document to a caller-selected local path. The current implementation:

1. Requires a fully qualified local drive-letter path.
2. Requires the parent directory to exist.
3. checks the directory ancestry for reparse points.
4. rejects an existing evidence target and refuses to overwrite.
5. opens the target with `FileMode.CreateNew`, which atomically fails if the target name already exists.
6. writes UTF-8 without a BOM and calls `Flush(true)` before disposing the stream.

These controls provide useful path-policy, no-overwrite, and file-data-flush guarantees. They do **not** pin the checked parent directory. A directory or ancestor can potentially be replaced after the reparse-point check and before the file is created. Therefore the current sequence does not eliminate a directory-replacement / time-of-check-to-time-of-use (TOCTOU) race.

## Decision

Do not describe the current evidence writer as race-free or fully protected against directory replacement.

A future implementation may claim this property only after it:

- opens the intended parent directory and retains a handle to that exact directory object;
- verifies the opened directory's identity and rejects reparse-point traversal according to an explicitly tested policy;
- creates the evidence file relative to that retained directory handle (rather than resolving the original path again);
- uses create-new semantics and restrictive sharing for the new file;
- verifies the created file's identity and that it is a regular file, not a reparse point;
- flushes file data and, where supported and required by the durability contract, the containing directory namespace;
- closes all handles deterministically and reports failures without silently falling back to path-based creation.

On Windows, a handle-relative native create operation (for example, a carefully reviewed `NtCreateFile` call with a parent `RootDirectory` handle) may be needed. Its status handling, access/share flags, object attributes, path encoding, and behavior on supported Windows versions must be reviewed and tested on Windows before adoption. A second path-based recheck is not an equivalent fix.

## Required verification before implementation is accepted

All tests must use a dedicated temporary host directory and synthetic evidence content. They must not open, enumerate, or access physical HDD device handles, and must not modify source VHDX files.

- Existing target: creation fails and original bytes remain unchanged.
- New target: exactly one file is created and its contents match the expected JSON bytes.
- Concurrent target creation: one creator wins; the other fails without truncating or replacing the winner.
- Reparse-point parent/ancestor: preflight rejects it.
- Directory replacement race: a controlled Windows test proves creation remains bound to the originally opened directory object, or safely fails; it must not silently write into a substituted directory.
- Flush/write failure: the operation reports failure and closes handles; no success result is emitted.
- Regression: existing preflight validator and disposable-VHDX tests remain green.

## Consequences

Until the handle-relative design is implemented and verified, the current adapter retains a documented residual race. Its existing `CreateNew` and `Flush(true)` guarantees remain valid but are narrower than race resistance or full namespace durability.

This ADR does not authorize physical HDD access, physical-device testing, source writes, mounting or dismounting VHDX files, VM state changes, formatting, initialization, partitioning, or repair operations. Public physical acquisition remains hard-disabled.
