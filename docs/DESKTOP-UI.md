# DiskRescue Pro Desktop UI — first milestone

The WPF desktop shell is a Windows-only front end for the existing read-only image inspection core.

## Current workflow

1. Select a regular disk-image file (.img, .bin, .dd, or .raw).
2. Select the logical sector size (512 bytes or 4096 bytes).
3. Run MBR/GPT inspection using the bounded read-only image reader.
4. Review partition rows and parser diagnostics.
5. Export inspection evidence as JSON.

## Safety limits

- The file picker selects files, not physical disks or volumes.
- The UI calls FileImageReader.Open; no physical-device API is referenced.
- The image reader opens with FileAccess.Read and exposes no write operation.
- The application does not mount, repair, initialize, format, repartition, or recover files.
- Export writes only the JSON report to a user-selected path; it does not write to the source image.
- This is an initial UI milestone, not a recovery product.

## Build and run on Windows

    dotnet build src/DiskRescue.Desktop/DiskRescue.Desktop.csproj -c Release
    dotnet run --project src/DiskRescue.Desktop/DiskRescue.Desktop.csproj

The Windows CI workflow builds the desktop project and publishes a self-contained win-x64 desktop preview artifact after tests pass.