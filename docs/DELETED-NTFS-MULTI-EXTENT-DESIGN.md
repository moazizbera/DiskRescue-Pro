# Deleted NTFS Multi-Extent Recovery — Engineering Design

## Status

Implemented in PR #9 as a separate deleted-file recovery path. The implementation supports resident streams and non-resident streams assembled from a resident $ATTRIBUTE_LIST when every referenced deleted FILE record is reachable through the initially validated MFT map.

The deleted-specific matcher validates the deleted base record, continuation record sequence and base reference, and complete attribute identity without weakening the existing live-file matcher. The deleted preflight assembles contiguous VCN extents, validates mapping pairs and physical overlap, assesses every physical run against $Bitmap, and revalidates the complete map and allocation evidence immediately before copying.

The synthetic end-to-end harness now includes a fragmented two-extent stream (VCNs 0 and 1 mapped to physical LCNs 30 and 34), exact output-byte comparison, recovery-manifest checks, source-image hash comparison, and refusal when a continuation-extent cluster is marked allocated. The recovery manifest records the validated physical run map for provenance.

Remaining limitations: non-resident $ATTRIBUTE_LIST bootstrap, recursive MFT discovery, sparse/compressed/encrypted streams, directory-index traversal, and physical-device partition recovery remain unsupported. A free $Bitmap bit is not evidence that the old content was never overwritten. Successful export and its SHA-256 do not prove original-content integrity.

## Required implementation

### 1. Separate deleted-record continuation validation

Add a deleted-specific matcher or an explicit, strongly typed validation mode. Do not change the default behavior of the existing live matcher.

For the deleted path:

- Require the base FILE record to be independently identified, marked not-in-use, and have a null base-file reference.
- Require each continuation FILE record to be marked not-in-use and to point back to the exact base record number and sequence.
- Validate the referenced record number and sequence from every attribute-list entry.
- Match the complete attribute identity: type, ordinal UTF-16 name, attribute ID, and lowest VCN.
- Reject duplicate entries, ambiguous record numbers, missing records, stale sequences, mismatched base references, malformed attributes, and incomplete matching.
- Apply existing record-count, aggregate-byte, attribute-parse, and comparison-work limits.
- Never treat an extension record as an independent deleted file.
- Preserve existing parent-record and filename identity checks on the base record.

### 2. Assemble the deleted data stream

- Collect only fully matched $DATA attributes for the requested stream name.
- Require all extents to be non-resident; reject resident/non-resident mixtures.
- Require the first extent to begin at VCN 0 and contain valid real, allocated, and initialized sizes.
- Sort by LowestVcn and require exact contiguous VCN ranges with no gaps, overlaps, duplicate extents, or overflow.
- Parse every mapping-pairs array independently with the volume cluster-count bound.
- Require decoded cluster count to equal each extent's declared VCN span.
- Reject sparse, compressed, encrypted, and otherwise flagged streams.
- Reject physical LCN overlap across the complete assembled run map.
- Enforce aggregate extent and decoded-run limits before allocating or reading.
- Validate total mapped bytes against the base extent's size metadata.
- Use only the base extent's authoritative stream sizes; continuation extent size fields must not silently override them.

### 3. Revalidate allocation before content reads

- Read $Bitmap through the existing validated MFT record-6 path.
- Assess every physical run in the complete assembled map, not just the first extent.
- Require every referenced cluster to be currently free.
- Reject allocated, mixed, unknown, out-of-volume, duplicate, sparse, or malformed mappings.
- Do not interpret a free bit as evidence that old bytes were never overwritten.
- Re-read and revalidate the base record, all continuation records, attribute-list matches, run map, and allocation evidence immediately before copying. If any identity or mapping changes, abort before reading content.

### 4. Keep export behavior unchanged

- Keep the deleted export API separate from the live-file reader.
- Require an explicit caller byte ceiling and cancellation token.
- Use bounded buffers and validated volume/image boundaries.
- Emit zeros beyond initialized size.
- Write only to a new destination via a temporary file.
- Preserve existing output and manifest collision protections.
- Publish the recovery manifest only after a complete successful export.
- Remove only temporary/output files owned by the current operation if publication fails.
- Continue to report content integrity as unverified. The exported SHA-256 is a hash of exported bytes, not an original-file integrity check.

## Synthetic test matrix

All tests must use generated disposable image files; never use the original HDD or a user's physical source disk.

### Positive cases

- Deleted base FILE record with one continuation FILE record and two contiguous $DATA extents.
- Deleted base record with multiple continuation records and three or more contiguous extents.
- Fragmented physical allocation where VCNs are contiguous but LCNs are non-contiguous.
- Stream with initialized size smaller than real size; verify zero-fill after initialized size.
- Named data stream with exact ordinal stream-name matching.
- Successful export: compare output bytes to fixture payload, verify exported-byte SHA-256 and manifest, and verify source-image SHA-256 is unchanged.

### Negative and adversarial cases

- Continuation record incorrectly marked in-use.
- Continuation record points to a different base record or base sequence.
- Stale continuation sequence number.
- Missing continuation record or attribute-list entry.
- Duplicate attribute-list entry or ambiguous attribute identity.
- Attribute ID, type, name, or lowest-VCN mismatch.
- Continuation record outside the currently validated MFT map.
- Non-resident $ATTRIBUTE_LIST (until separately implemented and validated).
- First extent does not start at VCN 0.
- VCN gap, VCN overlap, duplicate extent, or arithmetic overflow.
- Mapping-pairs count disagrees with declared VCN span.
- Physical LCN overlap between extents.
- Sparse, compressed, encrypted, or mixed resident/non-resident stream.
- One or more clusters allocated, mixed, or unknown in $Bitmap.
- Bitmap truncation or bitmap resource-limit failure.
- Export byte ceiling exceeded; no output or manifest is published.
- Existing output or manifest collision; existing bytes remain unchanged.
- Cancellation and injected read/write/manifest-publication failures; no incomplete final output is left behind.
- Source-image hash remains unchanged on every success and refusal path.

## Acceptance gates

- Existing live-file $ATTRIBUTE_LIST tests remain unchanged and pass.
- Existing deleted single-extent recovery tests remain unchanged and pass.
- Deleted continuation validation has dedicated positive, negative, boundary, and adversarial tests.
- Every extent is covered by the allocation assessment and immediate pre-read revalidation.
- Full .NET test suite, synthetic recovery harnesses, WPF build, CLI publish, and Windows CI pass.
- Documentation explicitly distinguishes metadata validation, current allocation state, successful export, and original-content integrity.
- No physical HDD recovery claim is made; physical partition scanning/recovery remains disabled.

## Explicitly out of scope

- Non-resident $ATTRIBUTE_LIST bootstrap and recursive MFT discovery.
- Sparse, compressed, encrypted, or EFS recovery.
- Directory-index traversal and automatic full-tree reconstruction.
- Filename/path-based output until canonicalization and reparse-point protections are implemented.
- Any source-media writes, repair, formatting, initialization, or repartitioning.
- Any assertion that currently free clusters contain the original file bytes.
