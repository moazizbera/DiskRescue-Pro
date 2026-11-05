# DiskRescue Pro

**A safety-first Windows toolkit for disk inspection, lost-partition discovery, and data recovery.**

DiskRescue Pro is being built to help users understand what happened to a disk before they attempt repairs. The first release focuses on trustworthy, read-only inspection and recovery from disk images.

> **Early development:** This project is not a recovery utility and is not ready for real-world recovery. Do not use it on the only copy of important data. If a drive is clicking, repeatedly disconnecting, or showing hardware errors, power it down and consider professional recovery.

## Project principles

- **Read-only by default:** discovery and analysis must never modify the source device.
- **Image before intervention:** prefer working from a verified sector-by-sector image where practical.
- **Recover elsewhere:** never write recovered files to the source device.
- **Explain before action:** show evidence, confidence, and uncertainty; do not promise recovery.
- **Test without risking data:** use generated virtual disks and disposable disk images.
- **Transparent engineering:** document supported formats, limitations, and every write-capable operation.

## Planned capabilities

- Physical disk and volume inventory
- MBR and GPT structure inspection
- Lost or damaged partition candidate discovery
- NTFS, FAT, and exFAT metadata inspection
- Read-only file listing and file extraction to a separate destination
- Disk-image analysis and recovery reports

## Technology direction

- Windows desktop application using C# / .NET 10 (WPF)
- Separate core, device-access, partition, filesystem, and recovery layers
- Automated tests built around synthetic disk images
- Optional, carefully reviewed integrations with established open-source forensic libraries

The exact dependency set will be selected only after reviewing maintenance status, platform support, and license compatibility.

## Safety boundary

The current implementation reads regular image files only. It does not open physical devices and exposes no source-write API. A first command-line inspection preview is available on the feature branch: it emits a JSON report for image geometry, MBR entries, GPT primary/backup headers, partitions, and diagnostics. The initial product will not initialize, format, repartition, repair, or write partition tables. Any future write-capable feature must be isolated, disabled by default, require explicit informed confirmation, and be preceded by a verified backup or image. A UI warning is not a substitute for enforcing read-only access in the device-access layer.

## Repository layout

```text
docs/       Architecture, safety policy, roadmap, and development notes
src/        Application and domain projects
tests/      Unit, integration, and virtual-disk tests
tools/      Developer utilities and test-image generation
```

## Status

The feature branch now contains:
- A bounded, read-only regular-image reader.
- MBR and GPT parsing with CRC, geometry, bounds, overlap, and primary/backup validation.
- A WPF desktop preview for image selection, partition inspection, findings, and JSON report export.
- NTFS boot-sector, FILE-record fixup, attribute, and fragmented/sparse runlist parsers.
- An MFT reader that reads records through the initial validated `$MFT::$DATA` extent, including records crossing fragmented physical extents.
- Resident `$FILE_NAME` parsing and a bounded flat scan of initialized MFT records.
- A CLI `scan-ntfs` command for a flat MFT preview using an explicitly supplied volume byte offset and length. CI run 42090410822 passed an end-to-end smoke test against a generated NTFS image with a fragmented MFT extent and a `Report.txt` filename entry.

The successful Windows CI run [42089190267](https://github.com/moazizbera/DiskRescue-Pro/actions/runs/42089190267) validated the bounded MFT scan milestone, including tests, generated-image CLI smoke tests, WPF build, and Windows x64 publishing. Its associated test summary reported 48 passed and zero failed. The current branch also includes a sector-size consistency guard; its latest CI run is revalidating that final change.

**This is still an engineering preview, not a data-recovery utility.** Physical-disk access, lost-partition discovery, MFT extent continuation through `$ATTRIBUTE_LIST`, directory-index traversal, full file-tree construction, FAT/exFAT support, and file extraction are not implemented. The WPF desktop does not yet expose the NTFS scan. All validation so far uses synthetic images; manual Windows UI testing remains outstanding. Never experiment on the damaged source disk or the only copy of important data.
## First-version preview

See [docs/FIRST-VERSION.md](docs/FIRST-VERSION.md) for CLI usage, [docs/DESKTOP-UI.md](docs/DESKTOP-UI.md) for the initial WPF desktop workflow, and [docs/TEST-IMAGES.md](docs/TEST-IMAGES.md) for safe test images. Windows preview artifacts are published by successful CI runs.

## Contributing

Contributions should include tests, clear safety implications, and documentation of supported disk layouts and filesystems. Never include real user disk images, recovered personal files, credentials, or other sensitive data in issues or pull requests.

## License

The project license will be finalized after the initial dependency and distribution review.
