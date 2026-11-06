# DiskRescue Pro — Engineering Preview

## Supported workflows

### Image partition inspection

The Windows x64 CLI and WPF preview open regular image files read-only. The inspection workflow reports image size, MBR entries, GPT primary and backup validation, partition boundaries, and parser diagnostics. A validated backup GPT copy may be used when the primary header is damaged; the report preserves the warning.

### NTFS metadata preview

The `scan-ntfs` CLI command and WPF NTFS preview validate the supplied NTFS volume range, boot-sector geometry, MFT record 0, and the initial unnamed `$MFT::$DATA` extent. They read a bounded number of initialized MFT records through the initial extent, including fragmented physical runs, and decode resident `$FILE_NAME` values.

The preview builds a conservative partial tree from the records actually scanned. Parent sequence numbers are checked; unresolved or cyclic entries are reported as orphans. The tree is not complete because additional MFT extents and directory indexes are not yet followed.

### Attribute-list entry parsing

The filesystem library includes a bounded parser for resident `$ATTRIBUTE_LIST` values on already-parsed attributes and a one-level in-memory matcher for supplied, already validated FILE records. Matching checks the referenced record number and sequence, base-file reference, and full attribute identity: type, ordinal UTF-16 name, ID, and lowest VCN. Duplicate, stale, missing, malformed, mismatched, unsupported, or ambiguous references produce structured diagnostics. Parsing is limited to 16 MiB/65,536 entries; matching is limited to 4,096 candidate records, 16 MiB of record bytes, 4,096 parsed attributes, and 1,000,000 attribute comparisons.

This remains an in-memory matching layer only. It does not read referenced records from the image, discover further continuations, assemble MFT extents, support non-resident `$ATTRIBUTE_LIST` streams, or extend MFT coverage. A partial set of matches is never marked complete if any entry is rejected.

## Not supported

- Physical disk or volume enumeration and raw-device access.
- Lost-partition discovery or automatic partition reconstruction.
- Complete NTFS MFT coverage or directory-index traversal.
- FAT32 or exFAT parsing.
- File-content preview or recovery.
- Source repair, initialization, formatting, repartitioning, or writes.

## Build and test

Install the .NET 10 SDK:

```powershell
dotnet restore DiskRescue.sln
dotnet build DiskRescue.sln --configuration Release --no-restore
dotnet test DiskRescue.sln --configuration Release --no-build
```

Inspect a regular image file:

```powershell
dotnet run --project src/DiskRescue.Cli -- inspect "C:\Images\sample.img"
```

Preview NTFS metadata using an offset and length obtained from a trusted partition inspection:

```powershell
dotnet run --project src/DiskRescue.Cli -- scan-ntfs "C:\Images\sample.img" --offset 1048576 --length 1073741824 --max-records 5000
```

Never guess the volume offset or length. Do not supply physical-device paths.

## Generate safe fixtures

```powershell
.\tools\New-DiskRescueTestImages.ps1 -OutputDirectory "$env:USERPROFILE\Documents\DiskRescue-TestImages"
.\tools\New-NtfsMftPreviewImage.ps1 -OutputPath "$env:USERPROFILE\Documents\ntfs-mft-preview.img"
```

See [TEST-IMAGES.md](TEST-IMAGES.md) for expected output and WPF manual test steps. The synthetic NTFS fixture is a parser fixture, not a mountable NTFS volume.

## Download the Windows preview

Successful Windows CI runs publish:
- `DiskRescue-Pro-Desktop-win-x64-preview`
- `DiskRescue-Pro-win-x64-preview`
- `diskrescue-test-results`

Artifacts are available from [GitHub Actions](https://github.com/moazizbera/DiskRescue-Pro/actions/workflows/ci.yml).

## Validation

The post-merge Windows CI run [42095792708](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42095792708) passed all 53 tests, generated-image CLI smoke tests, WPF build, and Windows x64 desktop/CLI publishing.

Windows CI run [42097467414](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42097467414) validated the earlier parser-only milestone. The current matcher adds synthetic-record tests; full-solution CI, generated-image CLI smoke tests, and Windows x64 desktop/CLI publishing remain required for this change. Manual desktop interaction with generated images is still required.

## Data preservation

Use generated images or copies created for testing. Never test against the only copy of important data. If a physical drive clicks, overheats, repeatedly disconnects, or produces persistent I/O errors, stop using it and consult a reputable recovery specialist. Do not run CHKDSK repair, initialize, format, or repartition the source.
