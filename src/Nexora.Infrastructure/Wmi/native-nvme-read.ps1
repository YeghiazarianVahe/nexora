$ErrorActionPreference = 'Stop'
try {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NexoraNativeNvme {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern IntPtr CreateFile(string p, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool DeviceIoControl(IntPtr h, uint c, byte[] i, int il, byte[] o, int ol, out int r, IntPtr p);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  static void Put(byte[] b,int at,uint v) { BitConverter.GetBytes(v).CopyTo(b,at); }
  public static byte[] AtaRead(int drive,byte feature) {
    if(feature!=0xD0 && feature!=0xD1) return null;
    IntPtr h=CreateFile(@"\\.\PhysicalDrive"+drive,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
    if(h==new IntPtr(-1)) return null;
    try {
      byte[] input=new byte[33], output=new byte[528];
      Put(input,0,512); input[4]=feature; input[5]=1; input[6]=1;
      input[7]=0x4F; input[8]=0xC2; input[9]=0xA0; input[10]=0xB0;
      int returned;
      if(!DeviceIoControl(h,0x0007C088,input,input.Length,output,output.Length,out returned,IntPtr.Zero) || returned<528 || output[4]!=0) return null;
      byte[] data=new byte[512]; Array.Copy(output,16,data,0,512);
      return data;
    } finally { CloseHandle(h); }
  }
  public static byte[] Read(int drive,uint type,uint request,int length) {
    IntPtr h=CreateFile(@"\\.\PhysicalDrive"+drive,0,3,IntPtr.Zero,3,0,IntPtr.Zero);
    if(h==new IntPtr(-1)) return null;
    try {
      foreach(uint property in new uint[]{50,49}) {
        byte[] input=new byte[48+length], output=new byte[48+length];
        Put(input,0,property); Put(input,8,3); Put(input,12,type); Put(input,16,request);
        Put(input,24,40); Put(input,28,(uint)length);
        int returned;
        if(!DeviceIoControl(h,0x002D1400,input,input.Length,output,output.Length,out returned,IntPtr.Zero) || returned<48) continue;
        int offset=BitConverter.ToInt32(output,24), count=BitConverter.ToInt32(output,28);
        if(offset<40 || count<length || offset>returned-8-length) continue;
        byte[] data=new byte[length]; Array.Copy(output,8+offset,data,0,length);
        bool nonzero=false; foreach(byte b in data) if(b!=0) { nonzero=true; break; }
        if(nonzero) return data;
      }
      return null;
    } finally { CloseHandle(h); }
  }
}
'@
$disks = @()
foreach ($disk in (Get-CimInstance Win32_DiskDrive | Where-Object { [int]$_.Index -in @('@@INDEXES@@'.Split(',') | ForEach-Object { [int]$_ }) })) {
    $health = [NexoraNativeNvme]::Read([int]$disk.Index, 2, 2, 512)
    $identify = $null; $ata = $null; $thresholds = $null
    if ($null -ne $health) { $identify = [NexoraNativeNvme]::Read([int]$disk.Index, 1, 1, 4096) }
    else {
        $ata = [NexoraNativeNvme]::AtaRead([int]$disk.Index, 0xD0)
        if ($null -ne $ata) { $thresholds = [NexoraNativeNvme]::AtaRead([int]$disk.Index, 0xD1) }
    }
    if ($null -eq $health -and $null -eq $ata) { continue }
    $disks += [pscustomobject]@{ Index=[int]$disk.Index; Model=([string]$disk.Model).Trim(); Serial=([string]$disk.SerialNumber).Trim(); Health=$(if($null -ne $health){[Convert]::ToBase64String($health)}else{''}); Identify=$(if($null -ne $identify){[Convert]::ToBase64String($identify)}else{''}); Ata=$(if($null -ne $ata){[Convert]::ToBase64String($ata)}else{''}); Thresholds=$(if($null -ne $thresholds){[Convert]::ToBase64String($thresholds)}else{''}) }
}
$result = @{ Machine=$env:COMPUTERNAME; CollectedUtc=[DateTime]::UtcNow.ToString('o'); Disks=$disks }
} catch { $result = @{ Machine=$env:COMPUTERNAME; Disks=@(); Error=$_.Exception.Message } }
$json = $result | ConvertTo-Json -Depth 5 -Compress
[IO.File]::WriteAllText('@@OUTPUT@@.tmp', $json, (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath '@@OUTPUT@@.tmp' -Destination '@@OUTPUT@@' -Force
