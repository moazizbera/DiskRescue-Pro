# DiskRescue Pro Desktop — WPF Preview

The WPF application is a Windows front end over the read-only image and filesystem libraries. It is an engineering preview, not a complete recovery product.

## Current workflow

1. Select a regular image file (`.img`, `.bin`, `.dd`, or `.raw`).
2. Choose the logical sector size (512 bytes or 4096 bytes).
3. Inspect MBR/GPT metadata through the bounded image reader.
4. Review partition geometry and validation findings.
5. Select a partition and run the bounded NTFS metadata preview.
6. Review the partial directory tree, file-entry grid, filters, and selected-entry metadata. The scan is limited to initialized MFT records covered by the currently validated MFT mapping.
7. For supported eligible deleted NTFS entries, use the separate destination preflight and queued export workflow. The destination must resolve to physical storage separate from every disk backing the source image; unknown or virtual destinations fail closed.
8. Export the inspection JSON report to a separate user-selected path when needed.

## NTFS preview limitations

- The desktop preview does not recursively discover MFT continuation records or guarantee complete MFT coverage.
- It does not traverse `$INDEX_ROOT` or `$INDEX_ALLOCATION` directory indexes.
- It does not guarantee a complete directory tree or identify all deleted files.
- The deleted-NTFS export path supports only explicitly validated, bounded resident or supported non-resident streams and validated deleted continuation extents reachable through the current validated MFT mapping. Unsupported cases fail closed.
- A successful export and its SHA-256 describe the exported bytes; they do not prove the original content was never overwritten or is intact.

## Safety boundaries

- The primary image picker selects regular files, not physical disks or volumes. The separate physical-acquisition window is experimental and is an image-copy workflow only; it is not physical-device partition scanning or recovery.
- Image inspection and discovery are read-only. No mounting, repair, initialization, formatting, repartitioning, or partition-table writes are available.
- Deleted-NTFS export is a separate, narrowly scoped recovery workflow. It requires a fail-closed destination preflight and a destination on physical storage separate from the source image. Do not bypass this gate.
- JSON report export writes to a user-selected report path, never to the source image.
- Use generated fixtures or disposable copies only. Do not test on a damaged source or the only copy of important data. A hardware write blocker is recommended for irreplaceable/evidentiary media.

## Build and run

```powershell
dotnet build src/DiskRescue.Desktop/DiskRescue.Desktop.csproj --configuration Release
dotnet run --project src/DiskRescue.Desktop/DiskRescue.Desktop.csproj
```

The Windows CI workflow builds the WPF project and publishes a self-contained `win-x64` preview artifact after automated tests and generated-image smoke tests pass.
