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

The project has moved beyond documentation-only foundation. The current feature branch contains a bounded read-only image-file reader, initial MBR and GPT parsers with CRC and range validation, a reusable synthetic-image test builder, expanded parser tests, a JSON-reporting inspection CLI, and a Windows .NET 10 CI workflow. Windows CI runs 21 and 37 passed for the image-reader/MBR and GPT milestones respectively.

Physical-disk inventory, device identity/geometry reporting, lost-partition discovery, filesystem parsing, and recovery are not implemented. An initial WPF desktop shell for image selection, MBR/GPT inspection, partition review, diagnostics, and JSON report export is now included on the feature branch; it still requires CI validation and hands-on Windows testing. The current code has been validated against synthetic images only and is not ready for real-world recovery or testing on a user's disk.

## First-version preview

See [docs/FIRST-VERSION.md](docs/FIRST-VERSION.md) for CLI usage and [docs/DESKTOP-UI.md](docs/DESKTOP-UI.md) for the initial WPF desktop workflow. Windows preview artifacts are published by successful CI runs.

## Contributing

Contributions should include tests, clear safety implications, and documentation of supported disk layouts and filesystems. Never include real user disk images, recovered personal files, credentials, or other sensitive data in issues or pull requests.

## License

The project license will be finalized after the initial dependency and distribution review.
