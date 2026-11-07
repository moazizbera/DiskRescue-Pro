# Safe test images for DiskRescue Pro

Generate disposable test fixtures on Windows without touching any physical disk.

## Generate MBR and GPT fixtures

Open PowerShell in the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\New-DiskRescueTestImages.ps1
```

The script creates a new DiskRescue-TestImages folder in the current directory and refuses to overwrite existing fixture files. To choose another output folder:

```powershell
.\tools\New-DiskRescueTestImages.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-TestImages"
```

It creates three 51,200-byte images:
- `mbr-valid.img` — a valid MBR with one synthetic partition.
- `gpt-valid.img` — a valid protective MBR plus matching primary and backup GPT copies and one synthetic partition.
- `gpt-primary-header-damaged.img` — the primary header CRC is damaged; the backup copy remains valid and should expose the synthetic partition with a warning.

The script prints SHA-256 hashes after creation.

## Generate the synthetic NTFS MFT fixture

A separate generator creates a 65,536-byte parser fixture with a synthetic NTFS boot sector, a fragmented initial MFT extent, and one resident filename entry (`Report.txt`):

```powershell
.\tools\New-NtfsMftPreviewImage.ps1 -OutputPath "$env:USERPROFILE\Documents\ntfs-mft-preview.img"
```

Run the bounded MFT preview from the CLI:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "$env:USERPROFILE\Documents\ntfs-mft-preview.img" --offset 512 --length 65024 --max-records 4
```

Expected result: the image inspection shows one MBR partition. Select that partition in the WPF NTFS preview tab or run the CLI command above; the scan should return one entry named `Report.txt`. This confirms the synthetic MBR partition boundary, boot sector, MFT record fixups, fragmented MFT read, and resident filename parsing are connected. The fixture is deliberately minimal and is **not a mountable NTFS volume**. Do not mount it in Windows.

## Test in the desktop preview

1. Download the `DiskRescue-Pro-Desktop-win-x64-preview` artifact from a successful Windows CI run.
2. Extract it to a normal folder.
3. Run `DiskRescue.Desktop.exe`.
4. Select each MBR/GPT `.img` file and use 512-byte sectors.
5. Confirm the MBR fixture shows one MBR partition.
6. Confirm the GPT fixture shows one GPT partition and both GPT copies valid.
7. Confirm the damaged-primary fixture reports the primary header problem but displays the partition from the validated backup copy.
8. Export a JSON report to a different folder if you want to inspect the evidence.

These fixtures test metadata inspection and a narrow NTFS MFT parser path only. They do not test physical-device access, lost-partition discovery, full directory-tree traversal, file-content recovery, or the condition of a real disk.

## Safety

Never choose a physical disk or the damaged source drive. Do not initialize, format, repair, repartition, mount, or write to the source. If the drive is clicking, overheating, repeatedly disconnecting, or returning repeated I/O errors, stop and consider professional recovery.

## Lost NTFS partition discovery fixture

Generate a disposable image containing a valid NTFS volume that is deliberately absent from the MBR partition table:

```powershell
.\tools\New-LostNtfsPartitionImage.ps1 -OutputPath "$env:USERPROFILE\Documents\lost-ntfs-partition.img"
```

### One-command recovery-path test

From the repository root in PowerShell, run:

```powershell
.\tools\Test-LostNtfsRecovery.ps1
```

The script creates a uniquely named test folder under Documents, generates the disposable lost-partition image, runs discovery and bounded preview, extracts the synthetic resident file, and verifies its exact contents and SHA-256. It refuses to overwrite an existing image or recovered file. It uses only the generated image file and does not enumerate or access physical disks.

To choose another output folder:

```powershell
.\tools\Test-LostNtfsRecovery.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-Recovery-Test"
```

### First end-to-end CLI test

This is the recommended first recovery-path test. It uses only the generated image file and writes the recovered test file to a separate output folder.

```powershell
$image = "$env:USERPROFILE\Documents\lost-ntfs-partition.img"
$out = "$env:USERPROFILE\Documents\DiskRescue-Recovered"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 1. Discover the lost NTFS volume. This reads the image only.
dotnet run --project src/DiskRescue.Cli -- discover-ntfs $image --max-scan-bytes 4294967296

# 2. Preview the candidate at the reported byte offset and bounded length.
dotnet run --project src/DiskRescue.Cli -- scan-ntfs $image --offset 1048576 --length 65024 --max-records 4

# 3. Extract the synthetic resident file to a new destination.
dotnet run --project src/DiskRescue.Cli -- extract-ntfs-file $image --offset 1048576 --length 65024 --record 1 --output "$out\Report-recovered.txt" --max-bytes 1024

# 4. Verify the recovered test payload.
Get-Content "$out\Report-recovered.txt" -Raw
```

Expected results:
- Discovery reports one **Lost partition candidate** at byte offset `1,048,576`, with a valid corroborating backup boot sector and confidence score 95.
- The bounded metadata preview returns one entry named `Report.txt`.
- Extraction creates `Report-recovered.txt` in the separate output folder. Its expected contents are `DiskRescue synthetic recovered content`.

If the output file already exists, choose a new filename; extraction deliberately does not overwrite. The fixture and expected payload are synthetic test data. This test does not establish that arbitrary files on a damaged real disk can be recovered.

The desktop flow is also available: open the image, click **Inspect image**, then **Find lost partitions**. The scanner should report the same candidate. Select it to attempt the existing bounded NTFS metadata preview.

The fixture is a regular image file, not a physical disk. The scanner reads it only and does not update its MBR or GPT. The confidence score is evidence-based triage, not a probability of successful file recovery.
