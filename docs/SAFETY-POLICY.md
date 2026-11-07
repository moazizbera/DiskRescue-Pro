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

The desktop inventory now retrieves best-effort vendor/model/serial/bus metadata and maps enumerated Windows volume GUIDs to their physical disk extents. Volume enumeration is fail-closed: if any volume cannot be inspected or enumeration ends unexpectedly, no disk may be treated as free of mounted volumes. This mapping is not a stable hardware identity, does not prove the separate firmware boot device has been excluded, and cannot prevent Windows or other processes from writing through mounted filesystems. Disk numbers remain temporary locators. Physical sector scanning remains disabled; the physical layer does not yet provide end-user acquisition or physical-disk recovery.

A new Windows boot-device mapper resolves the running Windows directory to its volume GUID and inspects partition metadata for every enumerated volume. It marks the Windows installation disk, GPT EFI System Partition disks, and active/EFI MBR partition disks as boot-critical. Any failed or ambiguous volume partition query makes the result incomplete and blocks preflight. This is deliberately conservative and is not firmware boot-order attestation; the system may contain multiple EFI partitions and the mapper does not prove which firmware entry is selected. Physical imaging and sector scanning remain disabled until the full acquisition and boot-chain safety gates are met.

Do not test this feature against the user's damaged or only copy of a disk. The acquisition coordinator rejects identified boot-critical disks, incomplete volume/boot maps, missing or duplicated serials, removable devices, and unknown/virtual/RAID/layered bus types. It checks destination separation using both the volume map and live volume extents, then repeats the preflight immediately before opening the source. These controls reduce risk but do not prove firmware boot order or immutable source state. A raw handle opened with GENERIC_READ does not prevent Windows or another process writing through mounted volumes. A hardware write blocker is strongly recommended for irreplaceable or evidentiary media. The copy is a live acquisition, not a point-in-time snapshot. Before using it on valuable media, validate it on disposable virtual disks and representative Windows hardware, including cancellation, disk-full, I/O error, and manifest-publication failure cases.

## Engineering enforcement

- Device handles must be created using read-only access.
- The core API should expose read operations, not a general-purpose write method.
- Any future write-capable code must live in a separate assembly and process boundary, be disabled by default, and undergo a dedicated threat and safety review.
- Tests must assert that scan and recovery code cannot reach write operations.
- Logs must not contain recovered file contents or sensitive user data.

## User-facing emergency guidance

If the drive makes unusual mechanical noises, repeatedly disappears, becomes unusually hot, or produces persistent I/O errors: stop scanning and disconnect it safely. Do not run CHKDSK repair options, initialize the disk, format it, or create new partitions. If the data is valuable, consult a reputable recovery specialist.

If a disk has simply lost its partition entries and is otherwise healthy, minimize use and create an image to another disk before experimenting. Never save the image or recovered files onto the affected disk.


## Physical acquisition preflight (experimental)

PhysicalDiskImageAcquisition.Assess evaluates source identity, inventory completeness, volume-map completeness, boot-map completeness, boot-critical exclusion, serial uniqueness, removable-media status, and supported bus identity. Mounted volumes are not automatically treated as a blocker for imaging, but they produce an explicit warning: the operating system or another process may continue writing to them even though DiskRescue opens its raw handle read-only. The assessment is not an authorization for physical partition scanning.

The CLI list-physical-disks command is metadata-only. acquire-physical requires the disk number, exact reported serial, a new image path, a verified separate destination volume, and typed confirmation. The acquisition coordinator performs fresh preflight twice, opens the source read-only, copies in bounded chunks, aborts on any read error without zero-filling, verifies the completed image length and SHA-256 before publishing, and writes a JSON sidecar manifest. Existing image and manifest paths are never overwritten.

This first acquisition implementation is intentionally not resumable and has no bad-sector map or retry policy. It is not hardware write-blocked or point-in-time consistent. It is exposed in the desktop acquisition window and CLI, but has not been validated against physical hardware. A failure while writing the manifest can leave a verified image without its sidecar manifest; preserve the image and inspect its SHA-256 if this occurs. Physical partition scanning and recovery remain disabled. Hardware validation on disposable virtual disks and representative Windows devices is still required before the workflow should be used on valuable source media.

## Read-only image acquisition engine

The IO layer includes a bounded image-copy primitive for an already-open IReadOnlyRandomAccessSource. It reads sequentially in bounded chunks, writes only to a newly created destination-side temporary file, computes SHA-256 while copying, flushes the completed file, verifies its exact length and digest by reading it back, and publishes it without overwriting an existing destination. Cancellation and source/destination errors remove the temporary partial file when possible. The engine exposes no source-write operation.

## Image-based NTFS file-stream export

The filesystem layer can copy a validated resident or single-extent, non-sparse, uncompressed, unencrypted NTFS `$DATA` stream from a regular image file to a caller-owned output stream. The CLI requires an explicit maximum export size, uses a same-directory temporary output, flushes it, computes SHA-256, and publishes it only if the final path does not already exist. Cancellation and write failures remove the temporary output when possible.

This is deliberately not a complete file-recovery workflow. It does not yet traverse directory indexes, automatically reconstruct safe file paths, resolve multi-extent file streams, or support sparse, compressed, encrypted, or non-resident `$ATTRIBUTE_LIST` streams. The caller must select an output directory on storage separate from the suspect physical source; the image-only CLI cannot prove physical-device separation. Never export recovered data back to the affected source disk. Automated extraction tests use only synthetic disposable images.
