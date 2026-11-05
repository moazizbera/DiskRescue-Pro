# DiskRescue Pro

**A safety-first Windows toolkit for disk inspection, lost-partition discovery, and data recovery.**

DiskRescue Pro is an open-source project being built to help users understand what happened to a disk before they attempt repairs. The first release will focus on trustworthy, read-only inspection and recovery from disk images.

> **Early development:** This project is not yet a recovery utility. Do not use it on the only copy of important data. If a drive is clicking, repeatedly disconnecting, or showing hardware errors, power it down and consider professional recovery.

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

The initial product will not initialize, format, repartition, repair, or write partition tables. Any future write-capable feature must be isolated, disabled by default, require explicit informed confirmation, and be preceded by a verified backup or image. A UI warning is not a substitute for enforcing read-only access in the device-access layer.

## Repository layout

```text
docs/       Architecture, safety policy, roadmap, and development notes
src/        Application and domain projects
tests/      Unit, integration, and virtual-disk tests
tools/      Developer utilities and test-image generation
```

## Status

The repository is at the project-foundation stage. The first engineering milestone is a read-only disk inventory and image-inspection foundation, validated against synthetic test images before any real-device testing.

## Contributing

Contributions should include tests, clear safety implications, and documentation of supported disk layouts and filesystems. Never include real user disk images, recovered personal files, credentials, or other sensitive data in issues or pull requests.

## License

The project license will be finalized after the initial dependency and distribution review.
