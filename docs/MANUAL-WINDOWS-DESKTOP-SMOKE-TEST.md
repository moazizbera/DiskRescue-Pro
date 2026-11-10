# Manual Windows Desktop Smoke Test

Purpose: record an interactive desktop check of DiskRescue Pro against disposable disk-image fixtures. This checklist is a test procedure, not evidence that the manual test has already been performed.

## Safety rules

- Use only generated disposable image files from `tools/New-DiskRescueTestImages.ps1` and the NTFS fixture generators.
- Do not select a user's original HDD, USB drive, memory card, mounted system volume, or other valuable media.
- Do not initialize, format, repair, repartition, or write to any source image.
- Keep the source fixture hashes before and after the session. Export reports only to a separate disposable output folder.
- Do not use the experimental physical-disk acquisition workflow for this smoke test.

## Test environment

- Windows version/build: ____________________
- DiskRescue Pro commit: ____________________
- Tester/date: ____________________
- Desktop artifact/version: ____________________
- Fixture directory: ____________________

Generate fixtures from the repository root in PowerShell:

```powershell
$fixtures = Join-Path $env:TEMP "DiskRescue-Desktop-Smoke"
New-Item -ItemType Directory -Force -Path $fixtures | Out-Null
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\New-DiskRescueTestImages.ps1 -OutputDirectory $fixtures
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\New-NtfsMftPreviewImage.ps1 -OutputPath (Join-Path $fixtures "ntfs-mft-preview.img")
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\New-LostNtfsPartitionImage.ps1 -OutputPath (Join-Path $fixtures "lost-ntfs-partition.img")
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\New-FatPartitionImages.ps1 -OutputDirectory $fixtures
Get-ChildItem $fixtures -File | Get-FileHash -Algorithm SHA256 | Export-Csv (Join-Path $fixtures "source-hashes-before.csv") -NoTypeInformation
```

## Desktop workflow checks

| ID | Check | Expected result | Result / notes |
|---|---|---|---|
| UI-01 | Launch the desktop application without selecting an image | Dashboard renders; read-only mode is visible; no crash or modal error | |
| UI-02 | Select `mbr-valid.img` and inspect | One valid MBR partition is reported; offsets and sizes are within image bounds | |
| UI-03 | Select `gpt-valid.img` and inspect | Primary and backup GPT headers validate; selected partition list is populated | |
| UI-04 | Select `gpt-primary-header-damaged.img` and inspect | Damaged primary header is reported; valid backup GPT evidence is used conservatively | |
| UI-05 | Select `ntfs-mft-preview.img` and inspect NTFS preview | NTFS metadata preview shows the synthetic `Report.txt` entry; no write operation occurs | |
| UI-06 | Select `lost-ntfs-partition.img` and run lost-partition discovery | NTFS candidate near offset 1 MiB is reported with evidence/confidence; source remains unchanged | |
| UI-07 | Select `lost-fat32-partition.img` and run lost-partition discovery | FAT32 candidate near offset 1 MiB is reported; candidate row and evidence are visible | |
| UI-08 | Select `lost-exfat-partition.img` and run lost-partition discovery | exFAT candidate near offset 1 MiB is reported; unsupported file recovery is not implied | |
| UI-09 | Switch between Partitions and NTFS file preview tabs | Selection and findings remain coherent; unsupported data is clearly identified | |
| UI-10 | Export JSON inspection report where enabled | Report is created only at the chosen output path and contains the inspected image findings | |
| UI-11 | Cancel an available long-running scan using a disposable fixture | UI returns to a usable state; cancellation is not reported as successful validation | |
| UI-12 | Attempt an unsupported or malformed image | Clear validation/error message; application remains responsive; no source modification | |
| UI-13 | Resize/minimize/restore the window and inspect controls | No clipped essential actions, unreadable labels, or broken layout at supported display scale | |
| UI-14 | Close and relaunch the app | Clean shutdown and relaunch; no stale scan state or unexpected writes | |

## Post-test integrity checks

```powershell
Get-ChildItem $fixtures -File |
  Where-Object Name -notin @("source-hashes-before.csv", "source-hashes-after.csv") |
  Get-FileHash -Algorithm SHA256 |
  Export-Csv (Join-Path $fixtures "source-hashes-after.csv") -NoTypeInformation

$before = Import-Csv (Join-Path $fixtures "source-hashes-before.csv")
$after = Import-Csv (Join-Path $fixtures "source-hashes-after.csv")
Compare-Object $before $after -Property Path, Hash
```

Expected: no differences for source fixture files. Investigate any mismatch before accepting the test.

## Evidence to attach

- Screenshot of the initial dashboard.
- Screenshot of valid MBR inspection.
- Screenshot of valid GPT inspection.
- Screenshot of backup-GPT fallback findings.
- Screenshot of NTFS preview showing the synthetic file entry.
- Screenshot of lost-NTFS candidate evidence.
- Screenshot of lost-FAT32 and lost-exFAT candidate evidence.
- Before/after source SHA-256 comparison output.
- Windows version, app commit, tester, date, and any failures.

## Sign-off

- [ ] All applicable checks passed.
- [ ] All source fixture hashes are unchanged.
- [ ] No original or valuable physical media was accessed.
- [ ] Failures are recorded with reproduction steps and screenshots.

Tester: ____________________  Date: ____________________
