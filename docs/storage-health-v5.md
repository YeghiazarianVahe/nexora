# Storage health model 7 (legacy documentation filename retained)

Model 7 preserves SSD/NVMe percentages and separates HDD condition from operational severity. `DiskHealth:HddMethod=smart-raw` uses the published default multiplicative raw-attribute weights for 5/7/10/196/197/198; Seagate seek-rate operation counters are excluded and normalized failure thresholds still apply. Additional uncorrectable I/O/SMART 187 evidence remains visible as a warning without counting the same report twice. `HddCriticalSectorStatus=true` retains the existing critical-sector severity policy (pending >=6 or offline uncorrectable >=1 by default), independently of the numerical formula. Turning that status policy off still leaves a warning; it does not hide faults. Final organizational acceptance of severity thresholds is an owner decision, not an approval granted by code.

The independent method remains available. Its detailed penalty table below applies to that method; it must not be interpreted as the smart-raw HDD percentage algorithm. Temperature, age and interface CRC warnings never reduce condition percentage. Existing hardware reference skips are not manufactured as new acceptance evidence.

The API adds `currentHealthModelVersion`, `scanCompletedUtc` and `sourceNote`; existing `updatedUtc` is the source sample time when available. The UI derives earlier/unknown-model labeling from the backend contract, suppresses stale current temperature and preserves last-known health. Metadata is retained in the existing JSON reason ledger; there is no database schema migration for provenance. Native remote reads and local privileged cache are supported, replacing the local-only limitation described in the historical v5 implementation.

Local cache samples older than two minutes are stale; after ten minutes they are explicitly expired. Expired evidence can remain useful as last-known data and is never relabeled as a new measurement. Missing/unreadable local evidence retains the previous health with a collector warning. Fresh recovery replaces that warning with current evidence. Scan completion and source time are distinct. Native disk queries are read-only; execution creates temporary files/directories/processes and checks ACLs. See [operations](operations-v1.md).

Nexora computes an independent, explainable health estimate from available SMART, NVMe and Windows reliability evidence. No Hard Disk Sentinel proprietary code or algorithm is used. Agreement within five percentage points is an engineering validation target, **not a guarantee**. Confidence describes evidence coverage, not statistical error or remaining service life.

## Sources actually available

- Agentless WMI/CIM: `Win32_DiskDrive`, `MSFT_PhysicalDisk`, associated `MSFT_StorageReliabilityCounter`, `MSStorageDriver_ATAPISmartData`, `MSStorageDriver_FailurePredictStatus`, and `MSStorageDriver_FailurePredictThresholds`.
- Direct NVMe health log and Identify Controller: local IOCTL reads or a bounded DCOM caller-identity process on the target, plus a protected local SYSTEM collector cache when needed. ATA SMART receive-data is a fallback when identity-matched WMI SMART is unavailable. WMI and actually consumed native ATA evidence are counted once per drive; genuinely unmatched WMI records remain auditable.
- Records join by stable identity (serial, PnP/unique identity, then disk number), never array position. An ambiguous match stays unavailable.
- Corrected I/O retries (`ReadErrorsTotal` / `WriteErrorsTotal`) are no longer substituted for uncorrected errors. Temperature and hours alone are insufficient to establish health.
- Firmware is not persisted by the current collector. The additive API field is null and the UI shows Unavailable. Disk-to-system-volume association is also unavailable: Overview does not choose a primary SSD from enumeration order when several SSDs exist.

## Calculation

`health = clamp(cap - fault penalties, 0, 100)`; cap starts at 100. The existing configurable weights remain in `HealthWeights`.

| Evidence | Treatment |
| --- | --- |
| Standard NVMe percentage used | Prefer over Windows wear. Accept 0–255; remaining rated endurance is `100 - min(100, used)`. Exhausted endurance can display 0%; responding to I/O does not imply 10% life remains. |
| NVMe available spare / spare threshold | Require both in 0–100. Below threshold caps health at 20 and marks Critical. Within five points adds the configured five-point penalty. |
| NVMe critical warning | Validate byte range. Bits 0, 2, 3, 4, 5 mark health Critical and cap at 20. Bit 1 affects temperature only. |
| NVMe media/data-integrity errors | Require nonnegative count; penalty `min(25, round(5 + 4 × ln(max(1,count))))`. |
| Common SSD SMART 231 / 233 | Plausible normalized 0–100 remaining-life indicator. Heuristic interpretation; Medium confidence at most without a standard log. |
| Crucial/Micron/CT SSD SMART 202 | Normalized remaining-life percentage, only for recognized model prefixes. |
| Samsung SSD SMART 177 | Normalized remaining-life percentage, only for Samsung models. |
| ADATA SSD SMART 169 | Raw 0–100 remaining life when enabled by `DiskHealth:Adata169Enabled`; Low confidence. No artificial uplift to 50%. Disable when unsupported by a controller/vendor fixture. |
| Unknown-vendor 169 / 173 / 177 / 202 | Not interpreted as a universal life percentage. In particular, erase counts are not automatically life percentages. |
| Windows wear / explicit life | Accept 0–100; missing or out-of-range fields are not clamped into a healthy reading. Contradictory SMART life and Windows wear use the lower estimate and add `telemetry-conflict`. Standard NVMe takes precedence over conflicting Windows wear. |
| Windows Warning / Unhealthy | Cap at 60 / 20; Unhealthy is Critical. Healthy alone supplies no quantitative health estimate. |
| SMART failure prediction | Cap at 10, Critical. |
| SMART 5 reallocated sectors | Default tiers: 1→10, 10→20, 100→35, 500→45 deducted points. |
| SMART 197 / 198 | Concave penalties `min(40, round(15 + 6.2 × ln(count)))`. HDD pending ≥6 or uncorrectable ≥1 caps at 20 and marks Critical. |
| SMART 10 / 196 / 187 / 188 | Existing bounded spin-retry, reallocation-event, reported-uncorrectable and command-timeout penalties. The low word of packed attribute 188 is used. |
| SSD 171 / 172 / 181 / 182 | Existing bounded program/erase failure penalty. |
| Uncorrected read/write errors | Existing bounded penalty; unknown directions remain unknown in the UI. |
| SMART normalized failure threshold reached | Mark Critical, except temperature attributes 190/194, which cannot change health. |
| SMART 199 CRC | Interface warning only; no health deduction. |

Status thresholds remain OK ≥85, Warning 50–84, Critical <50 or an explicit critical fault. A 100% estimate from fault counters means no scored faults were reported; it is not proof of unused endurance. All score adjustments appear in `reasons[]`; `100 + sum(delta) == healthPercent` whenever a percentage exists.

When the log has invalid standard NVMe fields, or only temperature/hours/Windows Healthy/unrecognized attributes exist, health is **Unavailable**. A single generic Windows Healthy flag no longer produces 100%.

## Temperature and freshness

- Accept current temperature only strictly above 0 and below 120 °C. Invalid Windows values can fall back to a plausible SMART 194/190 low-byte reading.
- Keep temperature separate from health. Use valid drive WCTEMP/CCTEMP limits when both exist and warning < critical; otherwise use existing Admin class defaults (HDD 50/55, SSD 60/70, NVMe 70/80 °C).
- A scan older than 30 minutes, more than two minutes in the future, an offline endpoint, or an endpoint with a failed drive scan is stale. Suppress the *current* temperature/status and retain last-known health with a stale badge.
- Invalid new temperatures are stored as null; the last valid raw temperature within the requested retained history range is available separately as `lastKnownTemperatureC`. Bucket averages are never substituted for this raw last-known value. It is never silently promoted to a current measurement.
- History remains bounded by the existing range and retention policy. This does not add permanent last-good storage or more frequent WMI queries.
- Existing lifetime-max handling is retained; no NVMe lifetime maximum is invented. The legacy `estimatedDaysLeft` API remains for compatibility, but the console no longer presents its constant-wear extrapolation as a drive failure date.

## Confidence and model upgrades

- High: valid standard NVMe log, current model, no conflict or staleness.
- Medium: current-model SMART evidence, no conflict or vendor-specific ADATA interpretation.
- Low: unavailable estimate, earlier model, stale/failed/offline scan, conflicting telemetry, ADATA 169, or limited Windows-only reliability evidence.

Additive response fields: `confidence`, `valueKind`, `stale`, `lastKnownTemperatureC`, `firmware`, `healthModelVersion`. No database migration is required. Version 5 uses the existing bounded score-version rescan mechanism; this change does not deploy or trigger that mechanism on a live service. Older stored estimates remain Low confidence until a new scan.

## Validation limits

Synthetic tests cover missing, invalid, stale, contradictory, exhausted and vendor-specific data. Historical reference fixtures remain tests of those fixtures. Agreement with vendor tools on the actual workstation fleet, every SSD controller, firmware, RAID/USB bridge, remote WMI provider, and local IOCTL access is **NOT VERIFIED**. Missing physical capture fixtures remain skipped; no quantitative error bound has been established.
