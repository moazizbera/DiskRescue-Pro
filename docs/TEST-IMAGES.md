# Safe test images for DiskRescue Pro

Generate disposable test fixtures on Windows without touching any physical disk.

## Generate

Open PowerShell in the repository folder:

    powershell -ExecutionPolicy Bypass -File .\tools\New-DiskRescueTestImages.ps1

The script creates a new DiskRescue-TestImages folder in the current directory and refuses to overwrite existing fixture files. To choose another output folder:

    .\tools\New-DiskRescueTestImages.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-TestImages"

## Included fixtures

- mbr-valid.img — a valid MBR with one synthetic partition.
- gpt-valid.img — a valid protective MBR plus matching primary and backup GPT copies and one synthetic partition.
- gpt-primary-header-damaged.img — same GPT image with only the primary header CRC damaged; the backup copy remains valid and should still expose the synthetic partition with a warning.\n- ntfs-mft-preview.img — a 64 KiB parser fixture with a synthetic NTFS boot sector, a fragmented initial MFT extent, and one resident filename entry (Report.txt). It is not a mountable NTFS volume.

Each image is only 51,200 bytes. The script prints SHA-256 hashes after creation.

## Test in the desktop preview

1. Download the DiskRescue-Pro-Desktop-win-x64-preview artifact from a successful Windows CI run.
2. Extract it to a normal folder.
3. Run DiskRescue.Desktop.exe.
4. Select each .img file and use 512-byte sectors.
5. Confirm the MBR fixture shows one MBR partition.
6. Confirm the GPT fixture shows one GPT partition and both GPT copies valid.
7. Confirm the damaged-primary fixture reports the primary header problem but displays the partition from the validated backup copy.
8. Export a JSON report to a different folder if you want to inspect the evidence.

These fixtures test metadata inspection and a narrow NTFS MFT parser path only. The NTFS fixture is not a complete or mountable filesystem. It does not test physical-device access, lost-partition discovery, full directory-tree traversal, file-content recovery, or the condition of a real disk.

## Safety

Never choose a physical disk or the damaged source drive. Do not initialize, format, repair, repartition, mount, or write to the source. If the drive is clicking, overheating, repeatedly disconnecting, or returning repeated I/O errors, stop and consider professional recovery.

## Generate the synthetic NTFS MFT fixture

The separate NTFS generator creates only a disposable 64 KiB file. It does not open or enumerate physical devices:

    .\tools\New-NtfsMftPreviewImage.ps1 -OutputPath "$env:USERPROFILE\Documents\ntfs-mft-preview.img"

Run the bounded MFT preview from the CLI:

    dotnet run --project src/DiskRescue.Cli -- scan-ntfs "$env:USERPROFILE\Documents\ntfs-mft-preview.img" --offset 0 --length 65536 --max-records 4

Expected result: one flat entry named `Report.txt`. This confirms the synthetic boot sector, MFT record fixups, fragmented MFT read, and resident filename parsing are connected. It does not imply support for complete NTFS volumes or recovery of real files.

**Do not mount this synthetic image in Windows.** It is deliberately minimal and exists only to test the parser.
