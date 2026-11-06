# Architecture and Engineering Boundaries

## Product objective

DiskRescue Pro is a Windows-first, safety-first disk-image inspection and recovery project. The first responsibility is preserving evidence and protecting the source. A parser finding is evidence, not proof that a partition or file can be recovered.

## Implemented solution

```text
DiskRescue.sln
├── src/
│   ├── DiskRescue.IO/             Bounded read-only image-file access
│   ├── DiskRescue.Partitions/     MBR and GPT parsing
│   ├── DiskRescue.Filesystems/    NTFS boot, FILE, attribute, MFT and tree parsing
│   ├── DiskRescue.Cli/            Inspection commands and JSON reports
│   └── DiskRescue.Desktop/        WPF desktop preview
├── tests/
│   └── DiskRescue.IO.Tests/       Parser and generated-image tests
├── tools/                         Disposable image generators
└── docs/                           Safety, architecture, roadmap and test guides
```

The project structure is intentionally incremental. Do not add empty abstraction projects simply to match a proposed diagram.

## Layer responsibilities

### IO
- Opens regular image files using read-only random-access access.
- Exposes bounded reads through `IReadOnlyRandomAccessSource`.
- Rejects unsupported paths and invalid ranges.
- Does not expose a write method or physical-device implementation.

### Partitions
- Parses MBR entries and validates signature, boot flags, offsets, bounds, overlaps, and sector geometry.
- Parses GPT primary and backup copies independently.
- Validates header and partition-array CRC32, geometry, bounds, and copy consistency.
- May select a validated backup GPT copy when the primary header is damaged; diagnostics retain the discrepancy.

### Filesystems
- Validates NTFS boot-sector geometry against the supplied image range.
- Validates FILE record signatures and update-sequence-array fixups before exposing fixed-up bytes.
- Parses bounded resident and non-resident attribute headers.
- Decodes NTFS mapping pairs with VCN/LCN bounds and sparse-run awareness.
- Opens MFT record 0 and validates the initial unnamed `$MFT::$DATA` extent.
- Reads records through fragmented physical runs, bounded by initialized and mapped sizes.
- Parses resident `$FILE_NAME` values and parent references.
- Scans a bounded number of initialized records covered by the initial MFT extent.
- Builds a conservative partial tree with parent-sequence checks, cycle handling, and orphan reporting.
- Parses resident `$ATTRIBUTE_LIST` values from already-parsed attributes; non-resident list streams are rejected as unsupported.
- Matches parsed list entries against caller-supplied, already validated FILE records. It validates record number and sequence, base-file reference, and exact attribute type/name/ID/lowest-VCN identity, and returns structured diagnostics for rejected or ambiguous entries.
- Bounds list parsing to 16 MiB and 65,536 entries; matching is bounded to 4,096 records, 16 MiB of candidate record bytes, 4,096 parsed attributes, and 1,000,000 attribute comparisons per operation.
- The matcher performs no disk reads and does not discover or fetch referenced records. Current MFT reader can read references only if they fall within the initial validated MFT extent.
- `NtfsMftExtentMap` is an in-memory validator/assembler for already-decoded unnamed `$MFT::$DATA` extent attributes. It orders extents by lowest VCN, requires a VCN-0 base extent, validates attribute identity, runlist lengths, volume bounds, and exact no-gap/no-overlap continuity, and rejects sparse MFT runs. It has explicit extent/run limits. It does not read or authenticate continuation records, resolve `$ATTRIBUTE_LIST`, or change the reader's active mapping; callers must first establish record identity through independently validated input.

### CLI and desktop
- CLI emits structured JSON for image partition inspection and bounded NTFS metadata preview.
- WPF supports image selection, MBR/GPT inspection, partition selection, NTFS preview, findings, and JSON export.
- Presentation code does not implement raw-device access or parsing logic.

## Address and bounds model

- Image and volume offsets are signed 64-bit byte positions at the IO boundary; on-disk NTFS cluster and VCN fields are decoded as unsigned values.
- Every conversion and multiplication must be checked before issuing a read.
- Every partition and filesystem parser must enforce the caller-supplied image/volume boundary, not merely metadata-declared size.
- Sector size is explicit. Never silently assume 512 bytes.
- Untrusted counts, attribute lengths, run counts, recursion depth, and output sizes must have documented limits.
- Short reads, arithmetic overflow, malformed metadata, and unsupported compression/encryption must fail closed with useful diagnostics.

## Remaining NTFS continuation work

A safe continuation resolver must not treat an attribute-list file reference as sufficient proof of identity. The bounded matcher now enforces the identity checks below for records already supplied by its caller. The full resolver must additionally:

1. Read referenced records only through a validated, bounded MFT mapping, then validate their FILE fixups.
2. Discover list entries recursively only if cycle and depth limits are defined and tested.
3. Decode continuation runlists and enforce exact VCN continuity, no overlap, no gaps, and volume bounds.
4. Ensure initialized-size and real-size semantics are respected before scanning records.
5. Add non-resident `$ATTRIBUTE_LIST` stream support with separate bounds and tests.
6. Preserve diagnostics for every accepted or rejected extent and ensure a partial scan is never reported as complete.

`NtfsAttributeListParser` and `NtfsAttributeListMatcher` are separate in-memory primitives and are not wired into automatic MFT continuation discovery. They do not provide complete MFT extension coverage, fragmented-file reconstruction, file extraction, or recovery. Non-resident attribute-list streams, reading references outside the initial MFT extent, recursive discovery, and continuation runlist assembly remain unimplemented.

## Recovery destination boundary (future)

A future extractor must:
- Accept a destination selected separately from the source image.
- Resolve canonical paths and reject source/destination identity or ancestry conflicts.
- Avoid following reparse points or writing through links outside the chosen destination.
- Create output files safely without overwriting existing data by default.
- Stream bounded reads and writes, handle short writes, cancellation, and interrupted operations.
- Verify output length and hashes where source metadata supports them.
- Write a manifest and audit log without logging recovered content.
- Never write to or repair the source.

## Test strategy

Use generated fixtures, never a user's only copy:
- Valid and malformed MBR/GPT metadata.
- Damaged primary GPT with a valid backup copy.
- NTFS boot geometry, FILE fixups, attributes, runlists, MFT records, resident filenames, and directory-tree edge cases.
- Truncated values, overflows, excessive counts, invalid references, and malformed UTF-16.
- End-to-end CLI smoke tests against disposable images.
- Future fuzzing with deterministic seeds, bounded runtime, and memory budgets.

## Release gates

A feature is not complete because it compiles. Require:
- Tests for valid, malformed, boundary, and adversarial input.
- Explicit limitations and useful diagnostics.
- Evidence that the source cannot be modified through the feature's interfaces.
- Windows CI build/test/package success.
- Manual UI testing with generated images before release claims.
- A separate safety review before physical-device access or any write-capable workflow.
