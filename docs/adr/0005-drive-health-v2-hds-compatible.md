# ADR-0005: Drive health v2 (score version 4) — HDS-compatible scoring, drive-reported temperature limits

Status: Accepted. Supersedes the scoring part of ADR-0004 (join by serial, ADATA cap and the "reasons" idea stay as ADR-0004 decided).

## Context
The service desk reads drive health in Hard Disk Sentinel (HDS) and expects Nexora to agree with it. HDS's exact algorithm is proprietary. Version 3
of our model matched the reference drives but had gaps: NVMe "percentage used" was discounted after 10 points, SMART 199 (cable errors) cost score
points although it says nothing about the drive, the NVMe critical-warning mask missed the "volatile memory backup failed" bit, temperature limits
were fixed in code and ignored the limits the drive itself reports, and the reasons did not add up to the score.

## Decision
This is a **documented, calibrated reimplementation of the approach of HDS, labelled "HDS-compatible" everywhere (code comments, this ADR, UI text). It is
not HDS's algorithm and must never be described as identical.** Calibration means: the formula is simple and explainable, and it is tuned until the
captured reference drives produce the values the service desk reads in HDS.

Health (0-100, `DiskHealthEvaluator`, `ScoreVersion` 3 -> 4):
- **HDD**: start 100, weighted non-linear (concave) penalties from SMART 5 (tiered by count), 196, 197, 198 (base hit at the first occurrence, then
  `scale * ln(count)`, capped) and 10. Pending >= 6 or any uncorrectable sector is Critical (floor 20) as before. 187/188/I/O errors stay as small secondary signals.
- **SATA SSD**: the vendor wear/life-remaining attribute first (231, 233, 202, 169, 177, 173 — chosen by vendor/model as before; the reason names the attribute),
  then penalties for reallocated / pending / uncorrectable sectors and program/erase failures. **The ADATA attribute-169 rule stays capped at Warning** until
  verified (audit N2, `DiskHealth:Adata169Enabled`).
- **NVMe** (health log): health = 100 - percentage used, then penalties for media/data-integrity errors (concave), available spare close to its threshold,
  Critical if spare is below its threshold or any health critical-warning bit is set (0 spare, 2 reliability, 3 read-only, **4 volatile backup**, 5 persistent memory).
  Bit 1 (temperature) is not a health matter: it raises the temperature status and is listed as a note.
- **SMART 199** (UDMA CRC) is an interface problem: reported as `interfaceWarning` ("check the cable or port") and as a 0-point reason, **never in the score**.
- **Reasons**: every drive carries `reasons[]` of `{code, text, delta}`. Codes are stable (the SMART attribute id, or `life`, `nvme-used`, `nvme-spare`,
  `nvme-warning`, `nvme-media`, `windows`, `predict`, `floor`, `io`, ...). **The deltas always add up to `score - 100`** (the 10% display floor and the ADATA
  cap appear as positive "given back" lines), so every number is auditable. A test enforces this.

Temperature (separate from health, never changes the score):
- Current: SMART 194/190 low byte, the Windows reliability counter, or the NVMe composite temperature (Kelvin).
- Limits: the drive's own **WCTEMP/CCTEMP** (NVMe Identify Controller bytes 266-267 / 268-269, Kelvin; 0 = not reported) when both are present and warn < crit
  (`source = "drive"`); otherwise the Admin-set defaults per class (`source = "default"`): HDD 50/55, SSD (SATA) 60/70, NVMe 70/80 C. Remote NVMe has no Identify data
  (see N16) and uses the defaults.
- Lifetime maximum: SMART 194/190 raw (min/max bytes, accepted only when consistent with the current value), else the Windows counter for SATA drives;
  **never invented for NVMe** (its health log has none).
- Defaults live in `PlatformSettings` (`diskTempHddWarnC/CritC`, `diskTempSsdWarnC/CritC`, `diskTempNvmeWarnC/CritC`): integers 20-100, warn < crit, Admin-only
  (the existing settings endpoint and policy), audited by the existing `settings.update` entry, and exposed to every role by `/api/thresholds`.
  A changed default applies to stored drives without a rescan; a drive's own limits do not change.

Collection: the same read-only paths as before — WMI `MSStorageDriver_*` for ATA and `IOCTL_STORAGE_QUERY_PROPERTY` for NVMe on the PC Nexora runs on, now also
reading Identify Controller. Pure parsers (`NvmeSmart.ParseHealthLog`, `NvmeSmart.ParseIdentify`, `SmartParser.ParseTable`) take raw bytes so captured data can be replayed in tests.
Drives are joined to their SMART/physical records by **serial number** (then PnP id, then disk number), never by enumeration order (ADR-0004).

Storage: additive migration `DriveHealthV4` adds `disk_health.ReasonsJson`, `InterfaceWarning`, `TempWarnC`, `TempCritC`, `TempThresholdSource`, `TempMaxLifetimeC`. Nothing is dropped or renamed.
The existing rescan on score-version change re-reads every PC (bounded by `Monitoring:MaxDiskScansPerCycle`).

## Calibration and the release gate
Reference values (HDS): wks-111 Micron 2500 512GB NVMe 96 (+-2), Seagate ST1000VX012 HDD 100 (+-1); wks-44 SSD 65 (+-3), HDD 100 (+-1).
Raw SMART is captured with `scripts/capture-smart.ps1` into `tests/Nexora.Application.Tests/Fixtures/Smart/*.json` and replayed by `HdsAcceptanceTests`.
**The fixtures gate the release**: a missing fixture shows as *Skipped* ("fixture not captured"), and the release is not done until all four cases run and pass. If a
case fails, weights in `HealthWeights` are tuned (configuration, not code) and this ADR is updated with the numbers.

## Behaviour changes versus version 3 (existing tests updated deliberately)
- NVMe health is `100 - percentage used` (v3 weighted points after the first 10 at 0.4). An NVMe at 80% used is now Critical (20), not Warning.
- NVMe critical-warning mask now includes bit 4 (0x3D, was 0x2D); bit 1 only affects temperature.
- SMART 199 no longer costs points (v3: up to 10). `HealthWeights.CrcMax` removed; `CrcTolerated` only chooses the wording.
- Pending/uncorrectable/196/10/media-error penalties are concave (`Base + Scale * ln(count)`); `PendingPerSector`/`UncorrectablePerSector` became `PendingScale`/`UncorrectableScale`, etc.
  The reference points (pending 5 = -25, reallocated tiers 1/10/100/500 = -10/-20/-35/-45) are unchanged. `NvmeFullWeightUsedPoints`, `NvmeWeightAfter` were removed.
- **HDD critical temperature 60 -> 55 C**; NVMe now has its own defaults (70/80, was the SSD 60/70). The `HddTemp*`/`SsdTemp*` members left `HealthWeights` for `PlatformSettings`.
- Penalties are whole points; the sum of reasons equals `score - 100`.

## Consequences
- Scores can differ from v3 for NVMe drives with wear above 10% and for HDDs with CRC errors; the fleet is rescanned on first start.
- Calibration is only as good as the captured fixtures; a new vendor quirk needs a new fixture and, if needed, a weight change recorded here.
- Remote NVMe health is still limited to the Windows reliability counter (audit N16): the health log and Identify data are readable only on the PC the server runs on.

## Constraints honored
C4 (agentless WMI/IOCTL, no WinRM), C5/C6 (settings change is Admin-only server-side and audited; no endpoint action involved), HTTP only, no AD change. No helper trust code touched.
