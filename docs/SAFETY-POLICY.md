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

## Current physical-device implementation boundary

The initial Windows physical-device layer is limited to bounded disk-number probing and a read-only random-access handle. It requests `GENERIC_READ` and never requests `GENERIC_WRITE`. The current code validates reported capacity and logical sector size when opening a selected device.

The desktop inventory now retrieves best-effort vendor/model/serial/bus metadata and maps enumerated Windows volume GUIDs to their physical disk extents. Volume enumeration is fail-closed: if any volume cannot be inspected or enumeration ends unexpectedly, no disk may be treated as free of mounted volumes. This mapping is not a stable hardware identity, does not prove the separate firmware boot device has been excluded, and cannot prevent Windows or other processes from writing through mounted filesystems. Disk numbers remain temporary locators. Physical sector scanning remains disabled; this work does not yet provide acquisition/imaging or file recovery.

Do not test this feature against the user's damaged or only copy of a disk. Before any real-device scan workflow is enabled, require verified system/boot-device exclusion (including a separate EFI/system partition), explicit handling of removable and virtual/RAID/Storage Spaces devices, complete volume/mount mapping, identity revalidation immediately before opening, I/O failure and cancellation behavior, and an imaging-first workflow. A raw handle opened with GENERIC_READ does not make the underlying device immutable while Windows or another process has mounted volumes on it. Prefer disposable virtual disks for end-to-end physical-device tests.

## Engineering enforcement

- Device handles must be created using read-only access.
- The core API should expose read operations, not a general-purpose write method.
- Any future write-capable code must live in a separate assembly and process boundary, be disabled by default, and undergo a dedicated threat and safety review.
- Tests must assert that scan and recovery code cannot reach write operations.
- Logs must not contain recovered file contents or sensitive user data.

## User-facing emergency guidance

If the drive makes unusual mechanical noises, repeatedly disappears, becomes unusually hot, or produces persistent I/O errors: stop scanning and disconnect it safely. Do not run CHKDSK repair options, initialize the disk, format it, or create new partitions. If the data is valuable, consult a reputable recovery specialist.

If a disk has simply lost its partition entries and is otherwise healthy, minimize use and create an image to another disk before experimenting. Never save the image or recovered files onto the affected disk.


## Physical scan preflight (not an authorization to scan)

The current desktop inventory evaluates each detected device with a conservative physical-scan preflight. It blocks when device identity cannot be revalidated by serial number, serials are duplicated, the bus is unknown or layered (RAID, virtual, file-backed virtual, or Storage Spaces), removable media is reported, inventory or volume enumeration has errors, the disk contains an enumerated volume, or system/boot exclusion and a verified separate image have not been established.

At this stage, system/boot exclusion and verified imaging are deliberately not asserted by the application. Therefore the preflight remains blocked and the physical sector scanner is not exposed. A complete volume-to-disk map is useful evidence, but is not by itself proof that a device is safe to scan.
