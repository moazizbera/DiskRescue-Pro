# DiskRescue Pro — First Version Preview

## What this version does

This is an early Windows x64 command-line and WPF preview for inspecting **regular disk-image files**. It opens the selected image read-only.

The `inspect` command emits a JSON report containing:
- Image length and selected logical sector size
- MBR signature, protective-GPT indicator, partition entries, and diagnostics
- GPT primary and backup header validity, partition entries, CRC validation results, and copy-consistency diagnostics

The new `scan-ntfs` command validates an NTFS volume boot sector and performs a bounded, flat scan of initialized MFT records covered by the initial validated `$MFT::$DATA` extent. It can report resident `$FILE_NAME` values and parent references. It does not build a directory hierarchy or recover file contents. The command passed an end-to-end CI smoke test against a generated 64 KiB NTFS fixture containing a filename entry that crosses fragmented MFT extents. The current branch adds a sector-size consistency guard that is being revalidated.

## What it does not do

- It does not enumerate or open physical disks or volumes.
- It does not scan a damaged physical drive.
- It does not locate lost partitions yet.
- It does not resolve additional MFT data extents through `$ATTRIBUTE_LIST`.
- It does not traverse NTFS directory indexes or build a complete file tree.
- It does not parse FAT32 or exFAT.
- It does not list file contents or recover files.
- It does not repair, initialize, format, repartition, or write to any source.

**Do not point this preview at a physical-device path. Do not experiment on the only copy of important data.** If a disk is clicking, repeatedly disconnecting, overheating, or returning repeated I/O errors, stop using it and consider professional recovery.

## Run from source

Install the .NET 10 SDK, clone the repository, then run:

```powershell
dotnet run --project src/DiskRescue.Cli -- inspect "C:\Images\sample.img"
```

For a 4Kn image:

```powershell
dotnet run --project src/DiskRescue.Cli -- inspect "C:\Images\sample-4kn.img" --sector-size 4096
```

To preview NTFS MFT records, provide the volume's byte offset and length. Obtain these from a trusted partition-inspection report; do not guess the values:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "C:\Images\sample.img" --offset 1048576 --length 1073741824 --max-records 5000
```

The NTFS scan output is a flat JSON list, not a verified folder tree. Its coverage is limited to the initial MFT data extent. Redirect reports to a separate output file if needed:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "C:\Images\sample.img" --offset 1048576 --length 1073741824 > ntfs-preview.json
```

## Downloadable preview

The Windows CI workflow publishes self-contained Windows x64 CLI and desktop previews as GitHub Actions artifacts:
- `DiskRescue-Pro-win-x64-preview`
- `DiskRescue-Pro-Desktop-win-x64-preview`

Download and extract the artifacts from a successful workflow run. Example:

```powershell
.\DiskRescue.Cli.exe inspect "C:\Images\sample.img"
```

Treat all findings as inspection evidence, not a guarantee that data can be recovered.

## Validation status

Windows CI run 42089190267 passed restore, build, automated tests, safe fixture generation, CLI smoke tests, WPF build, and Windows x64 publishing for the bounded MFT scan milestone. The run's associated test summary reported 48 passed and zero failed. CI run 42090410822 also passed the synthetic NTFS MFT image generation and `scan-ntfs` smoke test. A subsequent sector-size consistency guard is being validated by the latest CI run. Manual Windows desktop interaction with generated images is still required.
