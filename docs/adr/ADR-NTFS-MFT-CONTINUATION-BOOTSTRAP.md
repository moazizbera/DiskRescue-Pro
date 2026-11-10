# ADR: NTFS MFT continuation bootstrap boundary

- Status: Accepted — generic on-volume bootstrap is not implemented
- Date: 2026-11-10
- Scope: NTFS `$MFT::$DATA` continuation discovery
- Related: PR #19, PR #20; `NtfsMftReader.ExtendUsingResidentAttributeList`

## Context

The base FILE record 0 can contain a resident `$ATTRIBUTE_LIST` that references extension FILE records. Each entry identifies a referenced MFT record number, sequence number, attribute ID, type/name and lowest VCN. The extension FILE record contains additional non-resident mapping pairs for `$MFT::$DATA`.

The current reader can read a FILE record only when its byte range is covered by the currently validated MFT VCN-to-LCN map. If a referenced extension record lies beyond that map, the reader cannot obtain the extension record's mapping pairs without first knowing where that record is located.

This is a circular dependency:

1. The attribute list identifies the extension FILE record number.
2. Translating that record number into a volume byte offset requires the MFT extent map.
3. The missing extension FILE record is needed to extend that same map.

A record number and sequence number are identity metadata, not a physical location.

## Decision

Do not implement generic continuation bootstrap by guessing a physical location, scanning arbitrary volume clusters for `FILE` signatures, or treating unvalidated bytes as an MFT record.

The implementation must continue to fail closed when an attribute-list reference cannot be read through the currently validated MFT map.

The following are not sufficient location authorities by themselves:

- A `FILE` signature found by a raw volume scan.
- A record number or sequence number from `$ATTRIBUTE_LIST`.
- A stale or partial runlist from an unvalidated extension record.
- A plausible record header without a trusted physical location and full fixup/identity validation.
- `$MFTMirr` as a general solution: it mirrors only a small initial portion of the MFT and cannot be assumed to contain arbitrary extension records.

## Permitted future paths

A future feature may extend the map only if it receives an independently justified candidate location from one of these explicitly bounded sources:

1. A caller-supplied, externally acquired and independently validated MFT extent map, with provenance and source hash recorded.
2. A separately implemented NTFS metadata source whose format, consistency rules, and location authority have been formally specified and tested; it must not rely on the untrusted continuation record it is trying to locate.
3. A user-provided forensic map or acquisition manifest, treated as an untrusted hint until every physical range and record identity is validated against the source image.

None of these sources currently exists as a supported bootstrap provider in this product. Do not imply that the future paths are implemented.

## Mandatory validation once an authoritative candidate exists

Before accepting any candidate extension FILE record:

- Check the requested physical byte range is within the image/volume and all arithmetic is checked.
- Read exactly one boot-sector-defined FILE-record-sized unit.
- Apply and validate update-sequence fixups for every sector.
- Validate the `FILE` signature, record number, in-use state, sequence number and base-file reference.
- Validate record attribute boundaries, alignment, IDs, names, type and `LowestVCN`.
- Verify the attribute-list reference and extension attribute refer back to base record 0 with the expected sequence.
- Decode mapping pairs with checked arithmetic; reject sparse runs, zero/negative lengths, overflow, out-of-volume clusters, gaps, overlaps, duplicate/conflicting VCN ranges and physical-cluster overlap.
- Enforce explicit limits for candidate count, records, extents, run count, bytes read, parser work and total elapsed work.
- Assemble into a new immutable map; publish no partial map if any check fails.
- Preserve the original validated map unchanged on every failure.

## Required synthetic fixture matrix

- Continuation record inside the validated initial map — accepted only after identity and attribute matching.
- Continuation outside the initial map with no authoritative location — rejected without raw-volume scanning.
- Candidate location out of volume, unaligned, truncated or overflowing — rejected.
- Wrong FILE signature, fixup mismatch, record number, sequence number, in-use flag or base reference — rejected.
- Wrong attribute type, name, ID or `LowestVCN` — rejected.
- Duplicate, cyclic, conflicting, gap, overlap, sparse, malformed mapping-pair and resource-limit cases — rejected.
- Multiple valid extents supplied by an independently validated map — accepted only when the assembled VCN map and physical runs are continuous and non-overlapping.

## Consequences

- Some fragmented NTFS volumes remain partially inspectable or unrecoverable by this reader. This is an intentional correctness boundary, not a reason to guess.
- A future bootstrap provider requires its own threat model, parser, provenance model, tests and review before integration.
- Physical-device partition scanning/recovery remains disabled. Tests use generated images and disposable VHDX only.
- No source-media writes, formatting, initialization, repair, repartitioning or partition-table changes are permitted.
