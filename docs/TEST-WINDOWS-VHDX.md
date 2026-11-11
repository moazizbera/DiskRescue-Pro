# Windows disposable VHDX safety smoke test

This opt-in integration test validates one narrow Windows hardware boundary: a disposable, file-backed virtual disk is discovered through the existing read-only metadata probe and then rejected by the production physical-acquisition safety policy.

It does **not** validate physical HDD imaging, bad-sector handling, lost-partition recovery from a raw device, or hardware write-blocker behavior.

## Requirements

- Windows 10/11 or Windows Server with PowerShell 7.
- Administrator PowerShell session.
- .NET 10 SDK.
- The repository checked out locally with NuGet restore access.
- At least 256 MiB of free space in the temporary directory.

## Run

From the repository root in an elevated PowerShell 7 window:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\Test-WindowsVhdxSafety.ps1
```

The script creates a new 128 MiB expandable VHDX in a temporary folder, attaches it read-only, confirms Windows reports it as read-only and RAW, then runs the opt-in xUnit smoke test against its PhysicalDrive number.

The test checks that:
- Windows discovers the attached virtual disk through the existing bounded physical-device enumeration.
- The reported storage bus is Virtual (14) or File-backed Virtual (15).
- The production acquisition assessment refuses the device specifically because its storage bus is virtual/layered.
- The public acquisition API is called only to verify its hard-disabled boundary: it must throw `NotSupportedException` before opening a source handle, reading sectors, or creating an image/manifest.
- The test verifies that neither the requested image nor its sidecar manifest is created.
- No source sectors are read; this remains a discovery and fail-closed API-gate test, not an imaging or coordinator end-to-end test.

The script detaches and deletes its temporary VHDX after a successful detach. If detach fails, it deliberately preserves the VHDX and prints a warning rather than deleting an attached file.

## What this proves — and what it does not

A passing result validates discovery, fail-closed classification, and the public API's early disabled rejection for this Windows host and VHDX implementation. It does not prove that every storage controller reports bus identity correctly, that all firmware boot configurations are recognized, or that a physical disk can be imaged safely. The production policy continues to reject virtual, file-backed virtual, RAID, Storage Spaces, unknown, and ambiguous bus types.

No diskpart command in this test initializes, partitions, formats, mounts, or writes sectors to the VHDX. The VHDX is attached with the `readonly` flag. No real physical HDD is selected for acquisition.

## Safety boundary

Do not change this test to attach the VHDX read-write, read source sectors, or enable the physical acquisition method. The public API call is intentionally limited to verifying its immediate disabled rejection. Do not use the test script as a reason to test against the damaged HDD. Physical acquisition remains experimental until separate review and representative Windows hardware validation are complete.
