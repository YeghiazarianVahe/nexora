<#
.SYNOPSIS
    Captures raw SMART / NVMe health data of every physical drive of THIS PC into JSON fixture files. Read-only.
.DESCRIPTION
    One JSON file per physical drive: <host>-<index>-<model>.json (schema 1). Sources: root\wmi MSStorageDriver_FailurePredict*,
    SMART_RCV_DRIVE_DATA (read attributes / read thresholds), IOCTL_STORAGE_QUERY_PROPERTY (NVMe log page 2, identify controller),
    root\Microsoft\Windows\Storage MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter. Nothing is written except the JSON files
    in OutDir. Needs an elevated PowerShell (does not self-elevate). Windows PowerShell 5.1 compatible, no modules, no remoting.
.PARAMETER OutDir
    Output folder. Default: .\smart-capture-<COMPUTERNAME>-<yyyyMMdd-HHmmss>
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-smart.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Inv = [System.Globalization.CultureInfo]::InvariantCulture

# ---------- pure helpers (unit-tested by tests\scripts\Test-Scripts.ps1) ----------

function ConvertTo-HexString {
    # Lowercase continuous hex; $null / empty gives ''.
    param([byte[]]$Bytes)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return '' }
    $sb = New-Object System.Text.StringBuilder ($Bytes.Length * 2)
    foreach ($b in $Bytes) { [void]$sb.Append($b.ToString('x2', $script:Inv)) }
    return $sb.ToString()
}

function ConvertTo-SafeFileName {
    # Letters/digits only, runs of anything else become one '-'; never empty; max 40 chars.
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return 'unknown' }
    $s = ([regex]::Replace($Name.Trim(), '[^A-Za-z0-9]+', '-')).Trim('-')
    if ($s.Length -gt 40) { $s = $s.Substring(0, 40).Trim('-') }
    if ($s.Length -eq 0) { return 'unknown' }
    return $s.ToLowerInvariant()
}

function Get-NormalizedSerial {
    # Join key: upper-case letters and digits only (Win32 and MSFT format serials differently: underscores, trailing '.').
    param([string]$Serial)
    if ([string]::IsNullOrWhiteSpace($Serial)) { return '' }
    return ([regex]::Replace($Serial, '[^A-Za-z0-9]', '')).ToUpperInvariant()
}

function Get-PnpKey {
    # root\wmi InstanceName is the PNP device id plus a "_<n>" suffix.
    param([string]$Id)
    if ([string]::IsNullOrWhiteSpace($Id)) { return '' }
    return ([regex]::Replace($Id.Trim(), '_\d+$', '')).ToUpperInvariant()
}

function Get-Prop {
    # StrictMode-safe property read.
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function ConvertTo-NullableNumber {
    param($Value)
    if ($null -eq $Value) { return $null }
    try { return [double]::Parse([string]$Value, $script:Inv) } catch { return $null }
}

function Get-BusTypeName {
    param($Code, [string]$InterfaceType)
    $map = @{ 1 = 'SCSI'; 2 = 'ATAPI'; 3 = 'ATA'; 4 = '1394'; 5 = 'SSA'; 6 = 'FibreChannel'; 7 = 'USB'; 8 = 'RAID'; 9 = 'iSCSI'; 10 = 'SAS'; 11 = 'SATA'; 12 = 'SD'; 13 = 'MMC'; 14 = 'Virtual'; 15 = 'FileBackedVirtual'; 16 = 'StorageSpaces'; 17 = 'NVMe' }
    if ($null -ne $Code) {
        $k = [int]$Code
        if ($map.ContainsKey($k)) { return [string]$map[$k] }
    }
    if ([string]::IsNullOrWhiteSpace($InterfaceType)) { return 'Unknown' }
    return $InterfaceType
}

function Get-MediaTypeName {
    param($Code)
    if ($null -eq $Code) { return 'Unspecified' }
    switch ([int]$Code) { 3 { return 'HDD' } 4 { return 'SSD' } default { return 'Unspecified' } }
}

function Get-HealthStatusName {
    param($Code)
    if ($null -eq $Code) { return $null }
    switch ([int]$Code) { 0 { return 'Healthy' } 1 { return 'Warning' } 2 { return 'Unhealthy' } default { return 'Unknown' } }
}

function Get-OperationalStatusName {
    param($Codes)
    if ($null -eq $Codes) { return $null }
    $map = @{ 0 = 'Unknown'; 2 = 'OK'; 3 = 'Degraded'; 4 = 'Stressed'; 5 = 'PredictiveFailure'; 6 = 'Error'; 10 = 'Stopped'; 53250 = 'LostCommunication'; 53251 = 'NoMedia'; 53252 = 'InService'; 53253 = 'Starting' }
    $names = @()
    foreach ($c in @($Codes)) {
        $k = [int]$c
        if ($map.ContainsKey($k)) { $names += [string]$map[$k] } else { $names += $k.ToString($script:Inv) }
    }
    return ($names -join ',')
}

function Test-IsElevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal $id).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------- native read-only access ----------

function Initialize-NativeReader {
    if ('NexoraSmartIo' -as [type]) { return }
    # Windows PowerShell 5.1 compiles this with C# 5: no interpolation, no 'out var'.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class NexoraSmartIo
{
    // Read-only control codes only: SMART receive-drive-data and storage query-property.
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static IntPtr Open(int drive, uint access)
    {
        IntPtr h = CreateFile("\\\\.\\PhysicalDrive" + drive, access, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "open PhysicalDrive" + drive);
        return h;
    }

    private static void Put(byte[] b, int offset, uint v) { BitConverter.GetBytes(v).CopyTo(b, offset); }

    // feature 0xD0 = SMART READ ATTRIBUTE VALUES, 0xD1 = SMART READ ATTRIBUTE THRESHOLDS. Returns the 512-byte sector.
    public static byte[] AtaSmartRead(int drive, byte feature)
    {
        if (feature != 0xD0 && feature != 0xD1) throw new ArgumentException("only the SMART read features are allowed");
        IntPtr h;
        try { h = Open(drive, GENERIC_READ | GENERIC_WRITE); }   // the receive-data code is defined with read+write access
        catch (Win32Exception) { h = Open(drive, GENERIC_READ); }
        try
        {
            byte[] input = new byte[33];            // SENDCMDINPARAMS: cBufferSize(4) IDEREGS(8) drive(1) rsvd(3) rsvd(16) buffer(1)
            Put(input, 0, 512);
            input[4] = feature;                     // bFeaturesReg
            input[5] = 1;                           // bSectorCountReg
            input[6] = 1;                           // bSectorNumberReg
            input[7] = 0x4F;                        // bCylLowReg
            input[8] = 0xC2;                        // bCylHighReg
            input[9] = 0xA0;                        // bDriveHeadReg
            input[10] = 0xB0;                       // bCommandReg = SMART
            byte[] output = new byte[16 + 512];     // SENDCMDOUTPARAMS: cBufferSize(4) DRIVERSTATUS(12) buffer
            int returned;
            if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SMART_RCV_DRIVE_DATA");
            if (output[4] != 0) throw new InvalidOperationException("SMART driver error " + output[4] + ", IDE error " + output[5]);
            if (returned < 16 + 512) throw new InvalidOperationException("short SMART reply (" + returned + " bytes)");
            byte[] data = new byte[512];
            Array.Copy(output, 16, data, 0, 512);
            return data;
        }
        finally { CloseHandle(h); }
    }

    // StorageDeviceProtocolSpecificProperty = 50, StorageAdapterProtocolSpecificProperty = 49. NVMe: ProtocolType 3,
    // dataType 1 = identify (requestValue = CNS), 2 = log page (requestValue = log id).
    public static byte[] NvmeQuery(int drive, uint propertyId, uint dataType, uint requestValue, int length)
    {
        IntPtr h = Open(drive, 0);
        try
        {
            byte[] input = new byte[8 + 40];
            Put(input, 0, propertyId);
            Put(input, 4, 0);                       // PropertyStandardQuery
            Put(input, 8 + 0, 3);                   // ProtocolTypeNvme
            Put(input, 8 + 4, dataType);
            Put(input, 8 + 8, requestValue);
            Put(input, 8 + 12, 0);                  // sub value
            Put(input, 8 + 16, 40);                 // ProtocolDataOffset
            Put(input, 8 + 20, (uint)length);       // ProtocolDataLength
            byte[] output = new byte[8 + 40 + length];
            int returned;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length, output, output.Length, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "IOCTL_STORAGE_QUERY_PROPERTY property " + propertyId);
            if (returned < 48 + length) throw new InvalidOperationException("short NVMe reply (" + returned + " of " + (48 + length) + " bytes)");
            byte[] data = new byte[length];
            Array.Copy(output, 48, data, 0, length);
            return data;
        }
        finally { CloseHandle(h); }
    }
}
'@
}

function Read-NvmeData {
    param([int]$Index, [uint32]$DataType, [uint32]$RequestValue, [int]$Length)
    $last = $null
    foreach ($prop in 50, 49) {
        try { return [NexoraSmartIo]::NvmeQuery($Index, [uint32]$prop, $DataType, $RequestValue, $Length) }
        catch { $last = $_.Exception.Message }
    }
    throw $last
}

# ---------- per-drive capture ----------

function Get-DriveRecord {
    param($Drive, $PhysicalDisks, $FailureData, $FailureThresholds, [string]$HostName)

    $errors = New-Object System.Collections.Generic.List[string]
    $index = [int](Get-Prop $Drive 'Index')
    $model = ([string](Get-Prop $Drive 'Model')).Trim()
    $pnp = [string](Get-Prop $Drive 'PNPDeviceID')
    $serial = ([string](Get-Prop $Drive 'SerialNumber')).Trim().TrimEnd('.')
    $normSerial = Get-NormalizedSerial $serial

    # MSFT_PhysicalDisk: by serial number first, then by DeviceId = disk number. Never by list position.
    $pd = $null
    if ($normSerial) {
        $pd = @($PhysicalDisks | Where-Object { (Get-NormalizedSerial ([string](Get-Prop $_ 'SerialNumber'))) -eq $normSerial }) | Select-Object -First 1
    }
    if ($null -eq $pd) {
        $pd = @($PhysicalDisks | Where-Object { [string](Get-Prop $_ 'DeviceId') -eq $index.ToString($script:Inv) }) | Select-Object -First 1
    }
    if ($null -eq $pd) { [void]$errors.Add('windows: no MSFT_PhysicalDisk matched by serial or device id') }
    if (-not $serial -and $null -ne $pd) { $serial = ([string](Get-Prop $pd 'SerialNumber')).Trim().TrimEnd('.') }

    $busCode = Get-Prop $pd 'BusType'
    $bus = Get-BusTypeName $busCode ([string](Get-Prop $Drive 'InterfaceType'))
    $isNvme = ($bus -eq 'NVMe') -or ($pnp -match 'NVME')
    if ($isNvme) { $bus = 'NVMe' }

    $ata = [ordered]@{ available = $false; vendorSpecificHex = ''; thresholdsHex = ''; source = '' }
    $nvme = [ordered]@{ available = $false; smartLogHex = ''; identifyControllerHex = ''; source = '' }

    if (-not $isNvme) {
        $key = Get-PnpKey $pnp
        $dataBytes = $null; $thrBytes = $null
        try {
            $row = @($FailureData | Where-Object { (Get-PnpKey ([string](Get-Prop $_ 'InstanceName'))) -eq $key }) | Select-Object -First 1
            if ($row) { $dataBytes = [byte[]](Get-Prop $row 'VendorSpecific') }
            $trow = @($FailureThresholds | Where-Object { (Get-PnpKey ([string](Get-Prop $_ 'InstanceName'))) -eq $key }) | Select-Object -First 1
            if ($trow) { $thrBytes = [byte[]](Get-Prop $trow 'VendorSpecific') }
        }
        catch { [void]$errors.Add('ata wmi: ' + $_.Exception.Message) }

        if ($dataBytes -and $dataBytes.Length -gt 0) {
            $ata.source = 'root\wmi MSStorageDriver_FailurePredictData/Thresholds'
        }
        else {
            try {
                Initialize-NativeReader
                $dataBytes = [NexoraSmartIo]::AtaSmartRead($index, 0xD0)
                $thrBytes = $null
                $ata.source = 'IOCTL SMART_RCV_DRIVE_DATA'
            }
            catch { [void]$errors.Add('ata ioctl attributes: ' + $_.Exception.Message); $dataBytes = $null }
        }
        if ($dataBytes -and $dataBytes.Length -gt 0) {
            $ata.available = $true
            $ata.vendorSpecificHex = ConvertTo-HexString $dataBytes
            if (-not ($thrBytes -and $thrBytes.Length -gt 0)) {
                try {
                    Initialize-NativeReader
                    $thrBytes = [NexoraSmartIo]::AtaSmartRead($index, 0xD1)
                    if ($ata.source -notmatch 'IOCTL') { $ata.source += ' + IOCTL SMART_RCV_DRIVE_DATA (thresholds)' }
                }
                catch { [void]$errors.Add('ata ioctl thresholds: ' + $_.Exception.Message); $thrBytes = $null }
            }
            $ata.thresholdsHex = ConvertTo-HexString $thrBytes
        }
        elseif (-not ($errors | Where-Object { $_ -like 'ata*' })) {
            [void]$errors.Add('ata: no SMART data from WMI or IOCTL')
        }
    }
    else {
        $nvme.source = 'IOCTL_STORAGE_QUERY_PROPERTY protocol-specific'
        try {
            Initialize-NativeReader
            $nvme.smartLogHex = ConvertTo-HexString (Read-NvmeData -Index $index -DataType 2 -RequestValue 2 -Length 512)
            $nvme.available = $true
        }
        catch { [void]$errors.Add('nvme smart log: ' + $_.Exception.Message) }
        try {
            Initialize-NativeReader
            $nvme.identifyControllerHex = ConvertTo-HexString (Read-NvmeData -Index $index -DataType 1 -RequestValue 1 -Length 4096)
        }
        catch { [void]$errors.Add('nvme identify controller: ' + $_.Exception.Message) }
    }

    # Windows view: MSFT_PhysicalDisk + its reliability counter (association, no Storage module needed).
    $win = [ordered]@{ temperatureC = $null; wear = $null; readErrorsTotal = $null; writeErrorsTotal = $null; powerOnHours = $null; healthStatus = $null; operationalStatus = $null }
    if ($null -ne $pd) {
        $win.healthStatus = Get-HealthStatusName (Get-Prop $pd 'HealthStatus')
        $win.operationalStatus = Get-OperationalStatusName (Get-Prop $pd 'OperationalStatus')
        try {
            $counter = Get-CimAssociatedInstance -InputObject $pd -ResultClassName 'MSFT_StorageReliabilityCounter' | Select-Object -First 1
            if ($counter) {
                $win.temperatureC = ConvertTo-NullableNumber (Get-Prop $counter 'Temperature')
                $win.wear = ConvertTo-NullableNumber (Get-Prop $counter 'Wear')
                $win.readErrorsTotal = ConvertTo-NullableNumber (Get-Prop $counter 'ReadErrorsTotal')
                $win.writeErrorsTotal = ConvertTo-NullableNumber (Get-Prop $counter 'WriteErrorsTotal')
                $win.powerOnHours = ConvertTo-NullableNumber (Get-Prop $counter 'PowerOnHours')
            }
            else { [void]$errors.Add('windows: no reliability counter for this disk') }
        }
        catch { [void]$errors.Add('windows reliability counter: ' + $_.Exception.Message) }
    }

    $size = ConvertTo-NullableNumber (Get-Prop $Drive 'Size')
    $firmware = ([string](Get-Prop $Drive 'FirmwareRevision')).Trim()
    if (-not $firmware -and $null -ne $pd) { $firmware = ([string](Get-Prop $pd 'FirmwareVersion')).Trim() }

    return [ordered]@{
        schema      = 1
        capturedUtc = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $script:Inv)
        host        = $HostName
        index       = $index
        model       = $model
        serial      = $serial
        firmware    = $firmware
        busType     = $bus
        mediaType   = Get-MediaTypeName (Get-Prop $pd 'MediaType')
        sizeBytes   = $(if ($null -eq $size) { [long]0 } else { [long]$size })
        isNvme      = [bool]$isNvme
        ata         = $ata
        nvme        = $nvme
        windows     = $win
        errors      = @($errors)
    }
}

function Invoke-SmartCapture {
    param([string]$OutDir)

    if (-not (Test-IsElevated)) {
        Write-Host 'Not elevated. Re-run from an elevated PowerShell (Run as Administrator): powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-smart.ps1'
        return 1
    }

    $hostName = $env:COMPUTERNAME
    if ([string]::IsNullOrWhiteSpace($OutDir)) {
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss', $script:Inv)
        $OutDir = Join-Path (Get-Location).Path ('smart-capture-' + $hostName + '-' + $stamp)
    }
    if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path (Get-Location).Path $OutDir }

    $drives = @(Get-CimInstance -ClassName Win32_DiskDrive | Sort-Object { [int]$_.Index })
    $physical = @(); $failData = @(); $failThr = @()
    $global = New-Object System.Collections.Generic.List[string]
    try { $physical = @(Get-CimInstance -Namespace 'root\Microsoft\Windows\Storage' -ClassName MSFT_PhysicalDisk) } catch { [void]$global.Add('MSFT_PhysicalDisk: ' + $_.Exception.Message) }
    try { $failData = @(Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_FailurePredictData) } catch { [void]$global.Add('MSStorageDriver_FailurePredictData: ' + $_.Exception.Message) }
    try { $failThr = @(Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_FailurePredictThresholds) } catch { [void]$global.Add('MSStorageDriver_FailurePredictThresholds: ' + $_.Exception.Message) }

    if ($PSCmdlet.ShouldProcess($OutDir, 'Create output folder')) {
        if (-not (Test-Path -LiteralPath $OutDir)) { [void](New-Item -ItemType Directory -Path $OutDir -Force) }
    }

    $summary = @()
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($d in $drives) {
        try { $rec = Get-DriveRecord -Drive $d -PhysicalDisks $physical -FailureData $failData -FailureThresholds $failThr -HostName $hostName }
        catch {
            $rec = [ordered]@{ schema = 1; capturedUtc = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $script:Inv); host = $hostName
                index = [int](Get-Prop $d 'Index'); model = [string](Get-Prop $d 'Model'); serial = ''; firmware = ''; busType = 'Unknown'; mediaType = 'Unspecified'
                sizeBytes = [long]0; isNvme = $false
                ata = [ordered]@{ available = $false; vendorSpecificHex = ''; thresholdsHex = ''; source = '' }
                nvme = [ordered]@{ available = $false; smartLogHex = ''; identifyControllerHex = ''; source = '' }
                windows = [ordered]@{ temperatureC = $null; wear = $null; readErrorsTotal = $null; writeErrorsTotal = $null; powerOnHours = $null; healthStatus = $null; operationalStatus = $null }
                errors = @('capture failed: ' + $_.Exception.Message) }
        }
        if ($global.Count -gt 0) { $rec.errors = @($rec.errors) + @($global) }

        $file = '{0}-{1}-{2}.json' -f $hostName, $rec.index.ToString($script:Inv), (ConvertTo-SafeFileName $rec.model)
        $path = Join-Path $OutDir $file
        if ($PSCmdlet.ShouldProcess($path, 'Write drive capture')) {
            [System.IO.File]::WriteAllText($path, ($rec | ConvertTo-Json -Depth 5), $utf8)
        }
        $last4 = if ($rec.serial.Length -gt 4) { $rec.serial.Substring($rec.serial.Length - 4) } else { $rec.serial }
        $summary += [pscustomobject]@{ Index = $rec.index; Model = $rec.model; SerialLast4 = $last4; Bus = $rec.busType; Ata = $rec.ata.available; Nvme = $rec.nvme.available; Errors = @($rec.errors).Count }
    }

    Write-Host ''
    Write-Host (($summary | Format-Table -AutoSize | Out-String).TrimEnd())
    Write-Host ''
    Write-Host ('Output folder: ' + $OutDir)
    if ($drives.Count -eq 0) { return 2 }
    return 0
}

# Importable for tests: dot-sourcing defines the functions and runs nothing.
if ($MyInvocation.InvocationName -eq '.') { return }

exit (Invoke-SmartCapture -OutDir $OutDir)
