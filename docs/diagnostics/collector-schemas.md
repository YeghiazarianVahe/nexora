# F5 diagnostic collectors - output schemas (schema version 1)

Scripts: `src/Nexora.Application/Diagnostics/Collectors/<id>.ps1`. Fixtures: `tests/Nexora.Application.Tests/Fixtures/Diagnostics/<id>.{positive,negative,incomplete}.json`.
Field names are camelCase. Numbers are JSON numbers, dates are ISO-8601 UTC strings (`2026-10-15T08:30:00Z`). `?` after a name = may be absent; `|null` = always present, may be null.

## Contract (all collectors)

- Standalone Windows PowerShell 5.1 script, read-only, no modules, no remoting, no credentials, no network authentication. Runs as SYSTEM in a one-off scheduled task.
- Parameter `-BudgetSeconds` (int, default 25). When exceeded the script returns what it has with `incomplete:true, incompleteReason:"time budget"`.
- stdout = exactly one compact JSON object (`ConvertTo-Json -Depth 6 -Compress`). Diagnostics only via Write-Verbose.
- Output caps: lists <= 200 items, strings <= 260 chars, whole JSON < 20 000 chars (lists are halved until it fits). Any cap hit sets `truncated:true`.
- Each section is wrapped in try/catch; a failed section appends a short message to `errors` and leaves its fields at the empty/null value. Exit code is 0 whenever JSON was emitted.

### Common fields (always present)

| field | type | notes |
|---|---|---|
| `schema` | int | `1` |
| `collector` | string | kebab-case id = file name |
| `collectedUtc` | string | ISO-8601 Z |
| `incomplete` | bool | true when the time budget ran out (more sections may be empty) |
| `incompleteReason` | string\|null | `"time budget"` (network-probe also `"no targets"`) when incomplete, else null |
| `errors` | string[] | <= 20 short messages (<= 160 chars), no stack traces |
| `truncated` | bool | a cap was hit |
| `elapsedMs` | int | script runtime |

`userScope` (string `'full'|'partial'|'none'`) is present on collectors that read per-user data (outlook-files, outlook-addins, outlook-profile, pc-slow-startup). Profiles are taken from ProfileList (S-1-5-21-* SIDs) whose folder is under `%SystemDrive%\Users` with an ASCII-only name, excluding Default*/Public/All Users. `full` = every such profile's registry hive was loaded; `partial` = at least one hive not loaded (hives are never loaded by the script, so per-user registry data of logged-off users is missing; file-system data is still collected); `none` = no hive readable. Rules must treat `partial` as "absence is not evidence".

## 1. outlook-files

```
userScope, usersScanned:int,
files: [ { user:string, kind:'ost'|'pst'|'nst', path:string, sizeBytes:int|null, lastWriteUtc:string|null,
           onNetworkShare:bool, driveLetter:string|null, source:'filesystem'|'profile'|'both', exists:bool|null } ]
```
`source`: found in `%LOCALAPPDATA%\Microsoft\Outlook` or `Documents\Outlook Files` (filesystem), referenced by an Outlook 16.0 profile in a loaded hive (profile), or both. Network paths (UNC, or drive letter that is a mapped/remote drive) are never touched (no network auth): `sizeBytes`/`lastWriteUtc`/`exists` = null. `driveLetter` = single upper-case letter or null for UNC. E-mail addresses inside `path` (OST names are the mailbox SMTP address) are replaced with `<email>` (extension kept, e.g. `C:\Users\jdoe\AppData\Local\Microsoft\Outlook\<email>.ost`); Windows user names are kept. Rules must rely on `kind`, `sizeBytes`, `onNetworkShare`, `driveLetter` only.

## 2. outlook-addins

```
userScope,
addins: [ { progId:string, description:string|null, loadBehavior:int|null, manifestHint?:string, scope:'machine'|'user', user?:string, wow6432:bool } ],
resiliency: { disabledItems: [ { name:string, user?:string } ],
              slowAddins: [ { progId:string, durationMs:int|null, user?:string } ] }
```
LoadBehavior: 3 = load at startup (normal); 0/1/2 = unloaded/disabled/manual; 8/9/16 etc. are other states (rules decide). `resiliency.disabledItems` = text decoded from `Resiliency\DisabledItems` binary values (printable text only, capped). `slowAddins` = value names in `Resiliency\SlowAddinList` (duration from the DWORD value if > 0, else from `Resiliency\AddinLoadTimes`, else null). Per-user data comes from loaded hives only.

## 3. outlook-events

Application log, last 7 days, newest first, <= 500 events read per query.
```
windowDays:7,
events: [ { id:int, provider:string, timeUtc:string, messageShort:string(<=200),
            faultingModule?:string, exceptionCode?:string, appVersion?:string } ],
counts: { crash:int, hang:int, addinSlow:int, dotnetOffice:int }
```
crash = id 1000 with app OUTLOOK.EXE; hang = id 1002 with OUTLOOK.EXE; addinSlow = provider `Outlook` ids 45 and 59; dotnetOffice = ids 26/27/1026 from `.NET Runtime`/`Microsoft Office*`/`Office*` providers whose message mentions Outlook. `counts` count all matches in the window (not only listed events). `messageShort` (for 45/59/26/27/1026 = the first line of the message only, localized text as logged) has e-mail addresses, SIDs, user-profile folder names and `DOMAIN\user` tokens removed (`<email>`, `<sid>`, `C:\Users\<user>`, `<domain>\<user>`). `exceptionCode` e.g. `0xc0000005`.

## 4. outlook-process

```
count:int,
instances: [ { pid:int, workingSetBytes:int, privateBytes:int, handleCount:int, threadCount:int, cpuSeconds:number|null,
               responding:bool|null, startTimeUtc:string|null, sessionId:int } ]
```

## 5. office-version

```
installed: null | { productName:string, version:string, build:string, channel:string, channelRaw:string|null, buildReleaseDateUtc:null,
                    bitness:'x64'|'x86'|null, updatesEnabled:bool|null, lastUpdateCheckUtc?:string, installType:'ClickToRun'|'Msi' }
```
`installed:null` = no Office/Outlook found. `version` e.g. `16.0.17928.20114`, `build` = `17928.20114`. `channel` is a friendly name (`Current Channel`, `Current Channel (Preview)`, `Monthly Enterprise Channel`, `Semi-Annual Enterprise Channel`, `Semi-Annual Enterprise Channel (Preview)`, `Beta Channel`, `Office LTSC / perpetual`, `Unknown`, or `n/a` for MSI). `buildReleaseDateUtc` is always null (server maps build -> date).

## 6. outlook-profile

Best effort from the primary loaded user (first loaded hive that has Outlook 16.0 profiles). `null` = unknown (never guessed).
```
userScope,
profiles: { count:int|null, cachedMode:bool|null, downloadSharedFolders:bool|null, sharedMailboxesCached:null, additionalMailboxes:null,
            ostCount:int|null, exchangeCachedModeMonths:int|null },
perUser: [ { user:string, profileCount:int, cachedMode:bool|null, ostCount:int|null, exchangeCachedModeMonths:int|null } ]
```
`cachedMode`: true when the default profile references an OST (or policy `Cached Mode\Enable`=1); false only when policy `Enable`=0; otherwise null. `exchangeCachedModeMonths`: policy or user `Cached Mode\SyncWindowSetting` (months; 0 = all mail) else null. `sharedMailboxesCached` and `additionalMailboxes` are reserved and always null (no reliable registry source); `downloadSharedFolders` replaces them as the usable signal. `count` is null when no readable hive exists.

## 7. system-resources

```
disk: { systemDriveFreePercent:number|null, volumes: [ { letter:string, label?:string, sizeBytes:int, freeBytes:int, freePercent:number } ] },
memory: { totalBytes:int, freeBytes:int, usedPercent:number, pageFileUsedPercent:number|null },
cpu: { loadPercent:number|null, logicalProcessors:int },
search: { serviceState:string|null, startMode:string|null, indexing: { state:string|null, itemsToIndex:int|null, catalogSizeMB:number|null } }
```
Volumes = local fixed disks (DriveType 3). `indexing.state` and `itemsToIndex` are null (no WMI source exists); `catalogSizeMB` = size of `Windows.edb` when readable. `serviceState` e.g. `Running`/`Stopped`; `startMode` e.g. `Auto`/`Manual`/`Disabled`. Percentages 0-100 with <= 1 decimal.

## 8. network-probe

Parameters: `-BudgetSeconds`, `-Targets string[]` (max 5, each `^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$`; invalid/excess = skipped + error). ICMP + TCP connect only.
```
targets: [ { host:string, resolved:bool, dnsMs:int|null, addresses:string[](<=4), pingSent:int, pingReceived:int, lossPercent:number|null,
             minMs:int|null, avgMs:number|null, maxMs:int|null, tcp443:{ reachable:bool, ms:int|null }|null } ]
```
`tcp443` is null when the host did not resolve. If there is no valid target the collector returns `targets:[]`, `incomplete:true`, `incompleteReason:"no targets"`. 4 pings, 1 s timeout, sent to the first IPv4 address (else first address).

## 9. pc-slow-processes

Two CIM samples 2 s apart (Win32_Process kernel+user time). `cpuPercent` = share of total CPU capacity (0-100). Excludes the idle process.
```
sampleSeconds:number, logicalProcessors:int,
topCpu:    [ { name:string, pid:int, cpuPercent:number, workingSetBytes:int } ],   // <= 15, descending cpuPercent
topMemory: [ { name:string, pid:int, cpuPercent:number, workingSetBytes:int } ]    // <= 15, descending workingSetBytes
```

## 10. pc-slow-startup

```
userScope,
runKeys: [ { name:string, command:string, scope:'machine'|'user', user?:string } ],       // command = executable path, args stripped
startupFolderItems: [ { name:string, scope:'machine'|'user', user?:string } ],
runKeysTotal:int, startupFolderTotal:int,
autoServicesStopped:int,   // StartMode=Auto and not Running (includes delayed/trigger-start services that legitimately idle)
logonTasksCount:int|null,  // enabled scheduled tasks with a logon trigger; null if the cmdlet is unavailable
uptime: { lastBootUtc:string|null, days:number|null }
```
Run keys: HKLM and HKLM\WOW6432Node (machine), and HKU\<sid> for loaded hives (user). Startup folders: all-users and each ASCII profile (file system, so complete for logged-off users).

## 11. pending-reboot

```
signals: { cbsRebootPending:bool, windowsUpdateRebootRequired:bool, pendingFileRenameOperations:bool, computerRenamePending:bool, ccmRebootPending:bool|null },
pending:bool    // any signal true
```
`ccmRebootPending` is null when the ConfigMgr client (root\ccm\ClientSDK) is absent.
