# Disposable Hyper-V Crash-Test Preflight Contract

Status: design contract only. No VM controller or crash/recovery run is implemented by this document.

## 1. Safety boundary

This harness is for a newly generated, disposable Hyper-V guest and generated VHDX files only. It must never open, enumerate for acquisition, or pass a physical-disk device handle to DiskRescue. The acquisition source for crash tests is deterministic synthetic in-memory data. The only native destination session may target the guest's explicitly designated disposable NTFS data volume.

The host preflight is strictly read-only. It must not start, stop, pause, save, checkpoint, modify, attach, detach, mount, initialize, partition, format, repair, or delete any VM or disk. In particular, preflight must never call `Stop-VM`, `Dismount-VHD`, `Mount-VHD`, `Set-VM`, `Set-VHD`, `Initialize-Disk`, `New-Partition`, or `Format-Volume`.

If Hyper-V cmdlets are unavailable, permissions are insufficient, or any identity is ambiguous, the result is **Not Run**. Do not fall back to a host disk, a developer workstation disk, or a different VM.

## 2. Required explicit inputs

Every run must be bound to operator-supplied values, not inferred from the first matching VM:

- Exact Hyper-V VM ID (GUID), not only a display name.
- Expected VM name, used as a secondary consistency check.
- Exact full path of the guest boot VHDX.
- Exact full path of the single disposable test-data VHDX.
- Unique run ID and selected writer checkpoint phase.
- Expected generated-fixture length and SHA-256.
- A fresh baseline identity for the data VHDX, recorded before guest execution.

No wildcard path, partial VM name, automatic VM selection, or "first attached disk" selection is allowed.

## 3. Read-only host preflight

The preflight implementation may use only read-only Hyper-V inspection operations (for example, `Get-VM`, `Get-VMHardDiskDrive`, and `Get-VHD`) and ordinary file metadata/hash reads. It must:

1. Resolve exactly one VM by the supplied VM ID and require its name to match the supplied expected name.
2. Enumerate all hard-disk attachments for that VM. Require exactly one attachment whose normalized full path equals the supplied boot VHDX path and exactly one whose normalized full path equals the supplied data VHDX path. Reject additional, missing, duplicate, differencing, shared, pass-through, or otherwise unexpected disk attachments.
3. Require boot and data VHDX paths to be distinct, fully qualified local paths. Reject UNC/network paths, reparse-point path components, and path aliases that cannot be resolved unambiguously.
4. Inspect both VHDX metadata records and verify that each file exists, is a supported VHDX, and has the expected virtual-disk identity/type. The data VHDX must be a standalone generated disk, not a parent/child differencing chain.
5. Verify the data VHDX file's host-side identity, length, and SHA-256 against the recorded fresh baseline. Record the boot VHDX identity separately; never hash or modify it as a test target.
6. Record VM ID, name, generation, state, configuration version, attachment controller/location, canonical paths, VHDX IDs/types/sizes, host file identities, hashes, timestamps, run ID, phase, and preflight timestamp in a machine-readable evidence file.
7. Recheck the VM ID and attachment mapping immediately before handing control to a separately reviewed guest-runner. A changed VM state or attachment map invalidates the preflight.

The current read-only adapter re-queries the VM identity/state, complete attachment map, and both VHDX file identities/lengths/hashes/metadata after its initial inspection. Any observed drift invalidates the preflight. This is a point-in-time consistency check, not a lock: it cannot prevent a later change and does not eliminate the time-of-check/time-of-use window.

A preflight evidence file is a snapshot, not a capability or authorization token. It must never be treated as permission to stop a VM, start a guest runner, or perform recovery. Any future handoff must repeat the exact identity/attachment/baseline checks immediately before use and independently validate a fresh run marker. If the implementation cannot guarantee that sequencing, it must return **Not Run**.

Preflight must not change the VM state. It does not authorize a later shutdown by itself. The only host-side write performed by the adapter is creation of a new evidence JSON file in an existing, operator-selected local directory; it never writes to either VHDX.

## 4. Guest-runner prerequisites

A separate guest-runner may be designed only after the host preflight contract is implemented and reviewed. It must establish the guest identity and the mounted data volume using the run manifest and a guest-side volume identity record. It must prove that the guest destination maps to the one approved data VHDX and not the boot VHDX.

The writer must use deterministic synthetic bytes from an in-memory source. It must never call physical source discovery, open `\\.\PhysicalDrive*`, or use the host's disk number as a source selector. The destination must use the existing pinned native destination session and must remain inside the verified disposable data volume.

A checkpoint marker must be durable and host-readable and must include the exact run ID, phase, fixture length/hash, data VHDX identity, guest volume identity, and timestamp. A controller must refuse to act on a missing, stale, malformed, duplicate, or mismatched marker.

## 5. Destructive action is a separate authorization boundary

No abrupt VM power-off is part of preflight. A future controller may consider `Stop-VM -TurnOff` only after all of the following are independently verified:

- VM ID equals the exact preflight ID.
- VM is the expected disposable test VM.
- The current boot/data attachment map still matches preflight.
- The data VHDX identity and baseline match the run manifest.
- A fresh checkpoint marker matches the unique run ID and the one explicitly selected phase.
- No other VM or disk is in scope.

The controller must target the VM by exact ID, never use `-Force`, never target a host disk, and must stop on any mismatch. The operator must explicitly initiate each crash run. A marker is evidence for one phase only; it must not authorize a later phase or a different run.

## 6. Read-only recovery verifier

After a controlled guest restart, a separate verifier must inspect the same data VHDX without mounting it read-write, repairing it, replaying changes through a write-capable path, or deleting files. It must preserve all evidence, including temporary/orphan entries.

The report must include:

- Exact VM/run/phase and VHDX identity chain.
- Image and manifest presence, lengths, SHA-256 hashes, and parse/validation results.
- Expected versus observed publication state for the selected checkpoint.
- Any temporary, orphaned, duplicate, or unexpected directory entries.
- Guest and host logs plus tool versions and timestamps.
- A clear **Pass**, **Fail**, or **Not Run** result, with no automatic cleanup.

The verifier must not infer durability from a successful process exit or from a clean guest shutdown.

## 7. Required negative tests before any crash controller

The pure validator's mocked tests cover wrong VM ID; same name but different ID; boot/data path swap; duplicate attachment; unexpected third VHDX; missing attachment; differencing data VHDX; changed baseline hash; changed file identity; UNC path; reparse-point path; stale run ID; mismatched phase; unavailable VM inspection; VM not Off; duplicate virtual-disk identity; hard-link aliases; and shared/pass-through attachment rejection. The host adapter also fails closed when required Hyper-V attachment properties are absent or shared-disk metadata is null/non-Boolean. Hyper-V cmdlet availability and host permission failures are enforced by PowerShell prerequisites and terminating read-only queries; no fallback is permitted.

All negative tests must operate on mocked inspection results or generated fixtures. They must not attach, enumerate, or open physical disks.

## 8. Scope of any eventual evidence

A successful run can support only a narrowly scoped statement about the exact guest, Windows build, Hyper-V version, NTFS configuration, VHDX layout, checkpoint phase, and test procedure. It cannot prove host power-loss behavior, physical-media/controller cache persistence, other filesystems, or all Windows versions.

Until the preflight, guest-runner, controller, and read-only verifier are implemented and independently reviewed, crash/recovery execution remains **0% / Not Run**.
