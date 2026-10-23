using System.Runtime.InteropServices;
using System.Text;

namespace Nexora.Infrastructure.Wmi;

/// <summary>What an NVMe drive reports in its SMART / health log (log page 2).</summary>
public sealed record NvmeHealth(int CriticalWarning, int PercentageUsed, int AvailableSpare, int SpareThreshold, long PowerOnHours, long MediaErrors, long UnsafeShutdowns, int TemperatureC = 0)
{
    /// <summary>Remaining life as the drive counts it: 100 minus "percentage used" (which the drive may let run past 100).</summary>
    public int LifeLeftPercent => Math.Clamp(100 - PercentageUsed, 0, 100);
}

/// <summary>
/// What the NVMe Identify Controller structure says about temperature limits and identity. The limits are in Celsius; null when the drive
/// reports 0 ("not reported").
/// </summary>
public sealed record NvmeIdentify(int? WarningTempC, int? CriticalTempC, string? Model, string? Serial, string? Firmware);

/// <summary>
/// Reads the NVMe health log and Identify Controller data of a drive in THIS computer through the storage stack (IOCTL_STORAGE_QUERY_PROPERTY).
/// WMI reliability counters can report placeholder zero wear. RemoteNvmeReader performs the equivalent query on remote PCs.
/// Read-only; unavailable native data remains unavailable rather than proving 100% remaining life.
/// </summary>
public static class NvmeSmart
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageAdapterProtocolSpecificProperty = 49;
    private const uint ProtocolTypeNvme = 3, NvmeDataTypeIdentify = 1, NvmeDataTypeLogPage = 2, NvmeLogPageHealthInfo = 2, NvmeIdentifyCnsController = 1;
    private const int QueryHeader = 8, ProtocolDataSize = 40, LogLength = 512, IdentifyLength = 4096;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static NvmeHealth? TryRead(int physicalDriveNumber) =>
        Query(physicalDriveNumber, NvmeDataTypeLogPage, NvmeLogPageHealthInfo, LogLength, minimum: 192) is { } log ? ParseHealthLog(log) : null;

    /// <summary>Reads Identify Controller (CNS 1) of this PC's drive; null when the drive or the driver does not answer.</summary>
    public static NvmeIdentify? TryReadIdentify(int physicalDriveNumber) =>
        Query(physicalDriveNumber, NvmeDataTypeIdentify, NvmeIdentifyCnsController, IdentifyLength, minimum: 272) is { } data ? ParseIdentify(data) : null;

    private static byte[]? Query(int physicalDriveNumber, uint dataType, uint requestValue, int length, int minimum)
    {
        var handle = CreateFile($@"\\.\PhysicalDrive{physicalDriveNumber}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var input = new byte[QueryHeader + ProtocolDataSize];
            Put(input, 0, (uint)StorageAdapterProtocolSpecificProperty);
            Put(input, 4, 0);                                   // PropertyStandardQuery
            Put(input, QueryHeader + 0, ProtocolTypeNvme);
            Put(input, QueryHeader + 4, dataType);
            Put(input, QueryHeader + 8, requestValue);
            Put(input, QueryHeader + 16, ProtocolDataSize);     // ProtocolDataOffset
            Put(input, QueryHeader + 20, (uint)length);         // ProtocolDataLength

            var output = new byte[QueryHeader + ProtocolDataSize + length];
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out var returned, IntPtr.Zero)
                || returned < QueryHeader + ProtocolDataSize + minimum)
                return null;
            return output.AsSpan(QueryHeader + ProtocolDataSize, length).ToArray();
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Pure: decodes the SMART / Health Information log (NVMe base specification, log page 02h, 512 bytes). Null when the buffer is too short
    /// to hold the fields used (the first 176 bytes).
    /// </summary>
    public static NvmeHealth? ParseHealthLog(ReadOnlySpan<byte> log)
    {
        if (log.Length < 176) return null;
        return new NvmeHealth(
            CriticalWarning: log[0], PercentageUsed: log[5], AvailableSpare: log[3], SpareThreshold: log[4],
            PowerOnHours: ToLong(BitConverter.ToUInt64(log.Slice(128, 8))),
            MediaErrors: ToLong(BitConverter.ToUInt64(log.Slice(160, 8))),
            UnsafeShutdowns: ToLong(BitConverter.ToUInt64(log.Slice(144, 8))),
            TemperatureC: KelvinToCelsius(log[1] | (log[2] << 8)));
    }

    /// <summary>Same as <see cref="ParseHealthLog"/> for callers that hold a full log page; throws when the buffer is too short.</summary>
    public static NvmeHealth Parse(ReadOnlySpan<byte> log) =>
        ParseHealthLog(log) ?? throw new ArgumentException("The NVMe health log is shorter than 176 bytes.", nameof(log));

    /// <summary>
    /// Pure: decodes Identify Controller (4096 bytes): serial number (bytes 4-23), model (24-63), firmware (64-71), WCTEMP (266-267) and CCTEMP
    /// (268-269), both in Kelvin, where 0 means the drive does not report a limit. Null when the buffer is shorter than 270 bytes.
    /// </summary>
    public static NvmeIdentify? ParseIdentify(ReadOnlySpan<byte> identify)
    {
        if (identify.Length < 270) return null;
        return new NvmeIdentify(
            WarningTempC: LimitCelsius(identify[266] | (identify[267] << 8)),
            CriticalTempC: LimitCelsius(identify[268] | (identify[269] << 8)),
            Model: Ascii(identify.Slice(24, 40)), Serial: Ascii(identify.Slice(4, 20)), Firmware: Ascii(identify.Slice(64, 8)));
    }

    /// <summary>The composite temperature of the health log is in Kelvin; 0 means the drive does not report one.</summary>
    public static int KelvinToCelsius(int kelvin) => kelvin <= 0 ? 0 : kelvin - 273;

    private static int? LimitCelsius(int kelvin) => kelvin <= 273 ? null : kelvin - 273;

    private static long ToLong(ulong v) => (long)Math.Min(long.MaxValue, v);

    private static string? Ascii(ReadOnlySpan<byte> field)
    {
        var s = Encoding.ASCII.GetString(field).Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    private static void Put(byte[] buffer, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
