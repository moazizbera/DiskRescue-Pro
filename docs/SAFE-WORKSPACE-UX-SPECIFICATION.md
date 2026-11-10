# DiskRescue Pro — Safe workspace UX specification

**Status:** Approved design direction from the DiskGenius visual benchmark  
**Date:** 2026-11-10  
**Reference:** Owner-supplied DiskGenius V5.3 screenshots (main workspace, File, Partition and Disk menus)

## Product stance

DiskRescue Pro is a recovery and evidence-review workspace, not a partition manager. Adopt the reference application's information hierarchy and density, not its destructive command set.

## Target workspace

1. **Image navigator (left):** one opened image root, its validated partition-table entries, unallocated regions, and discovered candidates grouped separately. Show filesystem and size summaries. Clearly label the source as an image file.
2. **Partition map (top/center):** proportional partition segments, unallocated gaps, selected-state highlight, concise labels, and exact offset/length details on hover or keyboard focus.
3. **Partition inventory (center):** sortable columns for index, source (MBR/GPT/discovery), filesystem/type, status, start LBA, end LBA, byte offset and capacity. Do not fabricate unavailable geometry.
4. **Details inspector (right):** selected-item facts, validation evidence, confidence rationale, limitations and safe next steps. Keep observed, derived and unknown values distinct.
5. **Context actions:** selection-sensitive and allow-listed. A partition may be inspected or previewed; a discovery candidate may be compared and reviewed; an eligible deleted file may be assessed and queued. The UI never offers source mutation.

## Safe action vocabulary

Allowed: Open image, Inspect image, Find lost partitions, Preview filesystem, Compare candidate, View evidence, Search file entries, Check recovery destination, Queue eligible export, Cancel, Export JSON report.

Explicitly excluded: Format, Delete, Create partition, Resize, Extend, Split, Assign/remove drive letter, Change volume serial, Repair filesystem, Verify/repair bad sectors on source, Initialize, Convert MBR/GPT, Save partition table, Restore partition table, Mount source volume, or any command that writes to source media.

## Interaction rules

- One selected item drives all linked surfaces; selection changes clear stale details and stale recovery eligibility.
- Candidate discovery is evidence, not restoration. Use explicit labels such as “Lost partition candidate” and “Not written to image”.
- Show source path, image size, sector size and read-only state persistently.
- Keep destructive-looking actions absent, not merely disabled.
- Every long-running scan/export exposes progress and cancellation where safe.
- Errors explain what was refused and why; fail closed on unknown storage identity or incomplete evidence.
- Layout must resize within the Windows work area, retain readable minimum sizes and provide scroll access instead of clipping controls.
- Support keyboard operation, visible focus, high DPI and sensible text scaling.

## Visual direction

- Dark navy technical workspace with restrained cyan for selection/healthy validation and amber/red only for warnings.
- Compact but legible table density; align numeric values; avoid oversized cards that waste working area.
- Use clear hierarchy: navigator → map/inventory → details/evidence.
- Avoid imitating DiskGenius's crowded toolbar or legacy menu styling.
- Use consistent spacing, typography, focus and hover states across the main and acquisition windows.

## Verification matrix

| Resolution / scenario | Required checks |
|---|---|
| 1366×768 | No clipped controls; navigator and details remain usable; scrollable work area |
| 1920×1080 | Balanced workspace proportions; no excessive empty space |
| Constrained work area | Window remains inside work area; minimum dimensions adapt; content scrolls |
| Synthetic MBR/GPT image | Navigator, map, inventory and details stay synchronized |
| Synthetic lost-partition image | Candidate is visually distinct and never represented as restored |
| Image replacement | Old selection, evidence and recovery eligibility are cleared |
| Recovery queue | Destination gate, per-file state, cancellation and no-overwrite behavior remain intact |
| Safety review | No source mutation action or live physical partition scan is exposed |

## Non-negotiable safety boundary

All UI development and screenshots use generated disposable images. Physical-device partition scanning/recovery remains disabled. Experimental physical acquisition is a separate, unvalidated image-copy workflow and must not be presented as normal partition browsing or recovery.
