# ADR-0004: Drive health — join by serial, weighted scoring with reasons

Status: Accepted (seeded from design history — verify against code). The scoring part is superseded by ADR-0005 (join by serial and the ADATA cap still stand).

## Context
Joining SMART data to drives by enumeration order misclassified SSD/HDD health. Reference values: wks-111 (SSD 96% / HDD 100%), wks-44 (SSD 65% / HDD 100%), matching Hard Disk Sentinel.

## Decision
- Join SMART/NVMe data to physical disks by stable identity (serial number), never by index.
- Weighted health score in the style of Hard Disk Sentinel, with temperature kept separate from health and a per-drive "reasons" breakdown for auditability.
- ADATA SSD attribute rule is capped at Warning until verified with real data (audit N2).

## Consequences
- Scoring changes must be validated against the two reference PCs before release.

## Constraints honored
C4 (collection via WMI/IOCTL through permitted channels).
