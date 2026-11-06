# DiskRescue Pro Desktop — WPF Preview

The WPF application is a Windows front end over the read-only image and filesystem libraries. It is an engineering preview, not a complete recovery product.

## Current workflow

1. Select a regular image file (`.img`, `.bin`, `.dd`, or `.raw`).
2. Choose the logical sector size (512 bytes or 4096 bytes).
3. Inspect MBR/GPT metadata through the bounded image reader.
4. Review partition geometry and validation findings.
5. Select a partition and open **NTFS file preview**.
6. Request a bounded scan of up to 10,000 initialized MFT records covered by the initial validated `$MFT::$DATA` extent.
7. Review the partial tree and unlinked/orphan entries, then export the inspection JSON report if needed.

## NTFS preview limitations

- It does not follow additional MFT extents through `$ATTRIBUTE_LIST`.
- It does not traverse `$INDEX_ROOT` or `$INDEX_ALLOCATION` directory indexes.
- It does not guarantee a complete directory tree or identify all deleted files.
- It does not read or recover file contents.
- The separate `NtfsAttributeListParser` and `NtfsAttributeListMatcher` parse resident list values and match entries only against records already supplied in memory. They are not wired into the desktop scan, do not read continuation records, and do not extend MFT coverage.

## Safety boundaries

- The picker selects regular files, not physical disks or volumes.
- The current IO implementation opens images read-only and has no write API.
- No mounting, repair, initialization, formatting, repartitioning, or file recovery is available.
- JSON export writes to a user-selected report path, never to the source image.
- Use generated fixtures or disposable copies only. Do not test on a damaged source or the only copy of important data.

## Build and run

```powershell
dotnet build src/DiskRescue.Desktop/DiskRescue.Desktop.csproj --configuration Release
dotnet run --project src/DiskRescue.Desktop/DiskRescue.Desktop.csproj
```

The Windows CI workflow builds the WPF project and publishes a self-contained `win-x64` preview artifact after automated tests and generated-image smoke tests pass.
