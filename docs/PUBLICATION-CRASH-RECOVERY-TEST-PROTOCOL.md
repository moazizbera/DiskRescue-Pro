# Disposable NTFS VHDX crash/recovery acceptance protocol

**Status: design only. No crash test has been executed.**

This protocol is for a dedicated, disposable Hyper-V guest and generated VHDX files only. It is not authorization to access a physical HDD, and it does not enable the public physical-acquisition API. PR #30 remains draft/open/unmerged.

## 1. Scope and claims

The experiment evaluates NTFS recovery of the guest's disposable destination volume after an abrupt guest power-off at controlled publication checkpoints.

It can provide evidence about the tested Windows guest, NTFS version, Hyper-V configuration, virtual-disk format, and host storage stack. It cannot prove host power-loss behavior, controller/device cache persistence, or durability across all hardware, filesystems, filter drivers, and Windows versions.

Microsoft documents `Stop-VM -TurnOff` as equivalent to disconnecting VM power and warns of unsaved-data loss. Use it only against the uniquely identified disposable test VM. Do not use `Stop-VM -Force` as a substitute: that requests guest shutdown behavior rather than the same abrupt power-off experiment.

## 2. Isolation and preflight gates

Before any test run, an operator must:

1. Use a dedicated test host or an explicitly approved isolated Hyper-V environment. Do not use a production VM, a developer workstation VM containing valuable state, or a VM with unrelated virtual disks.
2. Create a uniquely named test VM and newly generated VHDX files in a dedicated temporary test directory. Record each VHDX full path, file identity, size, and SHA-256 before attachment.
3. Attach only the generated destination VHDX as the test data disk. Create its GPT/NTFS filesystem inside the guest. The test must not initialize, format, mount, or write to any host disk selected by disk number.
4. Verify from the host that the VM ID, VM name, attached VHDX path, and guest data-volume identity match the run manifest. Abort on missing, duplicate, ambiguous, or unexpected devices.
5. Verify that the guest OS disk and data VHDX are distinct. The only target for test writes is the newly created NTFS data volume. No source-device acquisition is performed.
6. Ensure no VM checkpoints, differencing disks, replication, backup jobs, or other mechanisms will silently revert or alter the test data. Preserve an untouched copy of the generated VHDX outside the VM before starting.
7. Confirm sufficient free host storage for the VHDX, preserved baseline, logs, and recovery copies. Do not use a physical-disk identifier as a target selector.

If Hyper-V is unavailable, permissions are insufficient, or any identity check cannot be proven, mark the test **Not Run**. Do not improvise with a physical disk.

## 3. Deterministic publication checkpoints

Use an internal checkpoint callback around the existing writer operations. The production coordinator must not expose or configure this callback; only an isolated test harness may supply it. The callback does not change the production publication sequence when omitted. Each run uses a unique run ID and writes a durable host-readable checkpoint record containing the run ID, phase, expected image hash, expected manifest hash (when known), and the guest VHDX identity.

Exercise these phases independently, restoring a fresh copy of the generated baseline before each run:

| Phase | Pause point | Purpose |
|---|---|---|
| A | After image data flush, immediately before image rename | Determine post-recovery state when only the unpublished temporary image exists |
| B | Immediately after image rename, before the image volume flush | Observe the image namespace after abrupt guest power-off at the publication boundary |
| C | After image volume flush and after manifest rename, before the manifest volume flush | Observe the two-entry publication state when the image flush completed but manifest flush did not |
| D | After manifest volume flush returns | Confirm the completed publication survives the guest crash/reboot test |

The checkpoint seam must pause only after recording its exact phase. The host controller must verify the checkpoint run ID and phase before issuing an abrupt power-off. A timeout, stale marker, unexpected phase, or mismatched VM/VHDX identity means **Abort**, not power-off.

## 3A. Test-only checkpoint seam

The writer now has an internal optional checkpoint callback with these exact phases:

- `ImageDataFlushedBeforeRename`
- `ImageRenamedBeforeNamespaceFlush`
- `ImageNamespaceFlushed`
- `ManifestRenamedBeforeNamespaceFlush`
- `ManifestNamespaceFlushed`

The production acquisition coordinator does not expose or configure this callback; normal production calls leave it null. The callback is intended for the isolated test harness to publish a run-ID/phase marker and wait for an external controller. It must not change the writer's ordering or perform filesystem mutations. A test pause after a rename must leave the published entry intact; it must never be translated into a volume-flush failure.

The deterministic writer tests assert the exact checkpoint order and verify that a synthetic pause failure immediately after image rename does not delete the published image or masquerade as a flush failure. These are unit-level seam tests, not crash/recovery evidence.

The protocol's Phase C maps to `ImageNamespaceFlushed` followed by `ManifestRenamedBeforeNamespaceFlush`; Phase D maps to `ManifestNamespaceFlushed`. The test harness must select one checkpoint per run and use a fresh VHDX baseline for every run.

## 4. Crash, reboot, and verification procedure

For each phase:

1. Start the guest and run one test acquisition against generated source bytes only. The test source is a generated fixture, not a physical disk.
2. Wait for the exact checkpoint record from this run. Confirm the guest VM ID and attached data VHDX still match the preflight manifest.
3. Issue `Stop-VM -Name <exact-test-vm-name> -TurnOff` only after all identity checks pass. Confirm the VM reaches Off state.
4. Do not mount or repair the VHDX from the host. Restart the same isolated guest and allow normal NTFS recovery to complete.
5. Run a read-only verifier in the guest. Record directory entries, file lengths, SHA-256 hashes, manifest parse/validation results, and any orphaned temporary files. Do not repair, rename, or delete evidence before collecting it.
6. Shut the guest down cleanly after evidence collection. Detach the data VHDX, verify detach, and preserve the VHDX plus logs as test artifacts. Never delete a VHDX whose detach state is uncertain.

## 5. Expected interpretation

- **Phase A:** No final image or manifest should be published. A temporary file may be present or may be removed by recovery/cleanup; record the observed state rather than assuming cleanup.
- **Phase B:** The final image name may or may not survive recovery. If present, its content must match the precomputed image hash. The manifest must not be treated as committed unless it independently exists and validates.
- **Phase C:** The image and manifest may have different persistence outcomes. Any surviving final image must match its expected hash. Any surviving manifest must parse and match the image identity/hash/length. A missing or mismatched sidecar is a failed consistency outcome requiring investigation.
- **Phase D:** Both final entries are expected to be present and valid after guest recovery. Any absence, hash mismatch, malformed manifest, or unexplained duplicate/orphan is a failure.

A successful run is evidence for this exact guest/virtual-disk configuration only. In particular, Phase D success does not establish host power-loss or physical-device power-loss durability.

## 6. Required evidence and acceptance

Retain:

- OS build, Hyper-V version, guest OS build, NTFS version, VM ID/configuration, and virtual-disk format/settings;
- generated VHDX paths, identities, baseline and post-run hashes;
- run ID, checkpoint phase, timestamps, and host power-off/restart state transitions;
- guest NTFS recovery events and verifier output;
- image/manifest hashes and directory listings before and after recovery;
- cleanup/detach confirmation.

Acceptance requires repeatable results across fresh baseline copies, no unexplained hash or metadata mismatch, and independent review of the protocol and native implementation. Any inconsistency is a blocker; do not weaken the fail-closed behavior to make the test pass.

## 7. Explicit exclusions

- No physical HDD/SSD device handles, raw physical-device reads, or physical-disk acquisition.
- No original, valuable, production, or user data.
- No host-volume crash, host power-cut, hardware power-loss, or controller-cache durability claim.
- No public physical-acquisition enablement, release approval, or change to the existing safety boundary.
- No claim that this experiment alone proves Windows rename namespace durability.

## References

- Microsoft Learn: [Stop-VM (Hyper-V)](https://learn.microsoft.com/en-us/powershell/module/hyper-v/stop-vm) — `-TurnOff` is an abrupt VM power-off and may lose unsaved data.
- Microsoft Learn: [Hyper-V Integration Services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/integration-services) — guest shutdown integration is distinct from forcing a VM off.
