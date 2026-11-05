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
- gpt-primary-header-damaged.img — same GPT image with only the primary header CRC damaged; the backup copy remains valid and should still expose the synthetic partition with a warning.

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

These fixtures test metadata inspection only. They do not test physical-device access, filesystem parsing, file recovery, or the condition of a real disk.

## Safety

Never choose a physical disk or the damaged source drive. Do not initialize, format, repair, repartition, mount, or write to the source. If the drive is clicking, overheating, repeatedly disconnecting, or returning repeated I/O errors, stop and consider professional recovery.
