# ADR: Race-Resistant Preflight Evidence Output

- Status: Handle-relative implementation in place; security verification incomplete
- Date: 2026-11-12 (implementation status updated 2026-11-13)
- Scope: `tools/Invoke-HyperVCrashPreflight.ps1` and `tools/PinnedEvidenceFile.cs`
- Safety classification: Host-side evidence output only; no source VHDX writes

## Context

The Hyper-V preflight adapter writes a JSON evidence document to a caller-selected local path. A path-based sequence of checking directory ancestry and then calling `FileMode.CreateNew` leaves a directory-replacement / time-of-check-to-time-of-use (TOCTOU) window: an ancestor can potentially be substituted after the check and before file creation.

## Decision

Use a Windows handle-relative evidence writer and fail closed. The current implementation in `tools/PinnedEvidenceFile.cs`:

- accepts only fully qualified drive-letter paths on a locally attached fixed drive;
- opens the volume root, then opens each directory component relative to its already-open parent handle;
- opens directory components with reparse-point opening semantics and rejects handles whose attributes identify a reparse point or non-directory;
- omits delete-sharing on the traversed directory handles, retaining those handles until the evidence file has been created. This is intended to prevent those directory objects from being renamed or deleted during the handle-relative create operation;
- creates the leaf relative to the retained final directory handle using `NtCreateFile` with `FILE_CREATE`, so an existing target is not replaced;
- uses restrictive sharing for the created file, returns the opened file handle as a `FileStream`, and has no path-based fallback;
- leaves durable file-data flushing to the caller, which writes UTF-8 and calls `Flush(true)` before disposing the stream.

The implementation is materially stronger than the former path-check plus path-based create sequence. However, it must **not** yet be described as fully race-proof or as providing complete namespace durability. The controlled directory-replacement regression test now passes in Windows CI. Explicit Windows-version/native-API review remains outstanding. The current flush contract covers file data; it does not claim that the containing directory namespace has been flushed to stable storage.

## Verification status

The Windows temporary-directory test currently covers:

- successful creation with exact expected bytes;
- refusal to replace an existing target, with original bytes preserved;
- rejection of an invalid traversal leaf;
- concurrent creation with exactly one winner and no overwrite.

The latest completed full CI run on commit `329d46b` passed. The following verification remains outstanding before making a broader race-resistance or durability claim:

- controlled directory replacement while the directory handle is retained, proving the operation remains bound to the originally opened directory or safely fails;
- nested reparse-point ancestor rejection against the native helper (including a junction used as an intermediate path component, with a child directory beneath its target);
- injected write and flush failures proving both exceptions propagate and the evidence stream is disposed (dedicated Windows CI and full workflow #1500 passed);
- review of native status handling, access/share flags, object attributes, and supported Windows-version behavior;
- a documented decision on whether directory namespace flushing is required and supported for this evidence contract.

All tests must use a dedicated temporary host directory and synthetic evidence content. They must not open, enumerate, or access physical HDD device handles, and must not modify source VHDX files.

## Consequences

The helper now performs handle-relative, no-replace creation and holds directory handles without delete-sharing through the create operation. First-level and nested junction rejection plus the controlled directory-replacement regression are covered by Windows CI. The nested test now places a real child path beneath the junction, ensuring the reparse point is an intermediate ancestor rather than merely the final directory component. This does not establish that every filesystem, Windows version, or namespace race is handled. Retain the qualification above until the broader native review and durability questions are resolved. `Flush(true)` provides the caller's file-data flush request; namespace durability is not claimed.

This ADR does not authorize physical HDD access, physical-device testing, source writes, mounting or dismounting VHDX files, VM state changes, formatting, initialization, partitioning, or repair operations. Public physical acquisition remains hard-disabled.
