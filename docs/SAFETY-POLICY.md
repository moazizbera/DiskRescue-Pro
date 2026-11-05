# Safety Policy

DiskRescue Pro handles storage devices that may contain irreplaceable data. Data preservation outranks convenience, speed, and feature completeness.

## Non-negotiable rules

1. Physical source devices are opened with read access only. The device-access implementation must not request write access.
2. No discovery, scan, preview, or recovery workflow may write to the source device.
3. Recovered output must be directed to a different physical device or an explicitly separate destination.
4. Never initialize, format, repartition, repair, or run filesystem repair commands against a user's source disk.
5. Prefer a verified sector image or clone before extensive scanning. Imaging a failing device can still worsen its condition; stop if it clicks, disconnects, overheats, or reports repeated I/O errors.
6. Do not imply that a scan is risk-free or that every lost partition/file can be recovered.
7. Treat device identifiers as unstable. Revalidate disk identity, capacity, and hardware identifiers before each session.
8. Bound all reads, validate offsets and lengths, handle short reads, and reject integer overflow before issuing I/O.
9. Never automatically mount a suspect volume read-write.
10. Test only with generated fixtures, disposable virtual disks, and images created for testing.

## Engineering enforcement

- Device handles must be created using read-only access.
- The core API should expose read operations, not a general-purpose write method.
- Any future write-capable code must live in a separate assembly and process boundary, be disabled by default, and undergo a dedicated threat and safety review.
- Tests must assert that scan and recovery code cannot reach write operations.
- Logs must not contain recovered file contents or sensitive user data.

## User-facing emergency guidance

If the drive makes unusual mechanical noises, repeatedly disappears, becomes unusually hot, or produces persistent I/O errors: stop scanning and disconnect it safely. Do not run CHKDSK repair options, initialize the disk, format it, or create new partitions. If the data is valuable, consult a reputable recovery specialist.

If a disk has simply lost its partition entries and is otherwise healthy, minimize use and create an image to another disk before experimenting. Never save the image or recovered files onto the affected disk.
