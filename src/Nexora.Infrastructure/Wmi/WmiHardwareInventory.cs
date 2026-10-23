using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;
using Nexora.Application.Services;

namespace Nexora.Infrastructure.Wmi;

/// <summary>
/// Hardware and OS inventory (read-only). A handful of small queries, each in its own section: a failing or missing class (for example no
/// SMBIOS memory data on a VM) leaves that part null and is reported in <c>Errors</c> instead of failing the whole read. Mapping rules live in
/// <see cref="HardwareMapping"/>.
/// </summary>
public sealed partial class WmiEndpointCollector : IHardwareInventoryCollector
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public Task<HardwareInventoryDto> CollectHardwareAsync(string host, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var session = Open(host);
            var op = Operation();
            var errors = new List<string>();

            T? Section<T>(string name, Func<T?> read) where T : class
            {
                ct.ThrowIfCancellationRequested();
                try { return read(); }
                catch (Exception ex) when (ex is CimException or InvalidOperationException)
                {
                    logger.LogDebug(ex, "Inventory section {Section} failed on {Host}", name, host);
                    errors.Add($"{name}: {ex.Message}");
                    return null;
                }
            }

            int? cpuArch = null;
            var cpu = Section("Processor", () =>
            {
                var procs = Query(session, op, "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, Architecture, MaxClockSpeed FROM Win32_Processor");
                if (procs.Count == 0) return null;
                var first = procs[0];
                cpuArch = Int(first, "Architecture");
                var cores = procs.Select(p => Int(p, "NumberOfCores")).Where(v => v > 0).ToList();
                var logical = procs.Select(p => Int(p, "NumberOfLogicalProcessors")).Where(v => v > 0).ToList();
                return new CpuInventory(HardwareMapping.CpuVendor(Str(first, "Manufacturer")), HardwareMapping.Clean(Str(first, "Name")),
                    cores.Count > 0 ? cores.Sum() : null, logical.Count > 0 ? logical.Sum() : null,
                    HardwareMapping.ProcessorArchitecture(cpuArch), Int(first, "MaxClockSpeed") is > 0 and var mhz ? mhz : null);
            });

            string? osArch = null, caption = null, kernelVersion = null;
            DateTime? installed = null, boot = null;
            long? usable = null;
            Section("Operating system", () =>
            {
                var os = Query(session, op, "SELECT Caption, Version, OSArchitecture, InstallDate, LastBootUpTime, TotalVisibleMemorySize FROM Win32_OperatingSystem").FirstOrDefault();
                kernelVersion = HardwareMapping.Clean(Str(os, "Version"));
                caption = HardwareMapping.Clean(Str(os, "Caption"));
                osArch = Str(os, "OSArchitecture");
                installed = Date(os, "InstallDate");
                boot = Date(os, "LastBootUpTime");
                usable = Long(os, "TotalVisibleMemorySize") is { } kb and > 0 ? kb * 1024 : null;
                return new object();
            });

            string? displayVersion = null, releaseId = null, edition = null, currentBuild = null;
            int? ubr = null;
            bool? fastStartup = null;
            Section("Windows release", () =>
            {
                displayVersion = RegString(session, CurrentVersionKey, "DisplayVersion");
                releaseId = RegString(session, CurrentVersionKey, "ReleaseId");
                edition = RegString(session, CurrentVersionKey, "EditionID");
                currentBuild = RegString(session, CurrentVersionKey, "CurrentBuild");
                ubr = RegDword(session, CurrentVersionKey, "UBR");
                return new object();
            });
            // Fast Startup makes LastBootUpTime the last FULL boot; a missing value stays unknown (null), never assumed.
            Section("Fast Startup", () =>
            {
                fastStartup = HardwareMapping.FastStartup(RegDword(session, PowerKey, "HiberbootEnabled"));
                return new object();
            });
            var osInfo = new OsInventory(caption, HardwareMapping.Clean(edition), HardwareMapping.WindowsVersion(displayVersion, releaseId),
                HardwareMapping.FullBuild(currentBuild, ubr), HardwareMapping.OsArchitecture(osArch, cpuArch), installed, boot, fastStartup, kernelVersion);

            long? physicalTotal = null;
            var computer = Section("Computer system", () =>
            {
                var cs = Query(session, op, "SELECT Manufacturer, Model, Name, Domain, PartOfDomain, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
                if (cs is null) return null;
                physicalTotal = Long(cs, "TotalPhysicalMemory") is > 0 and var t ? t : null;
                return new ComputerInventory(HardwareMapping.Clean(Str(cs, "Manufacturer")), HardwareMapping.Clean(Str(cs, "Model")), HardwareMapping.Clean(Str(cs, "Name")),
                    HardwareMapping.Clean(Str(cs, "Domain")), cs.CimInstanceProperties["PartOfDomain"]?.Value as bool?);
            });

            var memory = Section("Memory", () =>
            {
                List<MemoryModuleInfo> modules;
                try
                {
                    modules = Query(session, op, "SELECT Capacity, Manufacturer, PartNumber, DeviceLocator, BankLabel, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory")
                        .Select(m => new MemoryModuleInfo(HardwareMapping.Clean(Str(m, "DeviceLocator")), HardwareMapping.Clean(Str(m, "BankLabel")),
                            Long(m, "Capacity") is > 0 and var c ? c : null, HardwareMapping.Clean(Str(m, "Manufacturer")),
                            Int(m, "Speed") is > 0 and var s ? s : null, Int(m, "ConfiguredClockSpeed") is > 0 and var cc ? cc : null,
                            HardwareMapping.Clean(Str(m, "PartNumber")), HardwareMapping.MemoryTechnology(Int(m, "SMBIOSMemoryType"), Int(m, "MemoryType")))).ToList();
                }
                catch (Exception ex) when (ex is CimException or InvalidOperationException)
                {
                    errors.Add($"Memory modules: {ex.Message}");
                    modules = [];
                }
                var modulesReported = modules.Count > 0;

                int? slotTotal = null;
                long? maxCap = null;
                try
                {
                    var arrays = Query(session, op, "SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
                    var devices = arrays.Select(a => Int(a, "MemoryDevices")).Where(v => v > 0).ToList();
                    if (devices.Count > 0) slotTotal = devices.Sum();
                    var exKb = arrays.Select(a => Long(a, "MaxCapacityEx")).Where(v => v > 0).ToList();
                    var kb = arrays.Select(a => Long(a, "MaxCapacity")).Where(v => v > 0).ToList();
                    if (exKb.Count > 0) maxCap = exKb.Sum() * 1024; else if (kb.Count > 0) maxCap = kb.Sum() * 1024;
                }
                catch (Exception ex) when (ex is CimException or InvalidOperationException) { errors.Add($"Memory slots: {ex.Message}"); }

                long? installedBytes = modulesReported && modules.All(m => m.CapacityBytes is not null) ? modules.Sum(m => m.CapacityBytes!.Value) : physicalTotal;
                var (slots, occupied, empty) = HardwareMapping.Slots(slotTotal, modulesReported ? modules.Count : 0);
                if (!modulesReported) empty = null;   // without module data the occupied count is unknown, so the empty count is too
                var speeds = modules.Select(m => m.ConfiguredSpeedMhz ?? m.SpeedMhz).Where(v => v is not null).Select(v => v!.Value).ToList();
                var tech = modules.Select(m => m.Technology).Where(t => t is not null).Distinct().ToList();
                return new MemoryInventory(installedBytes, usable, tech.Count > 0 ? string.Join(" / ", tech) : null,
                    speeds.Count > 0 ? speeds.Max() : null, slots, modulesReported ? occupied : 0, empty, maxCap, modules, modulesReported);
            });

            var board = Section("Motherboard", () =>
            {
                var b = Query(session, op, "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
                return b is null ? null : new BoardInventory(HardwareMapping.Clean(Str(b, "Manufacturer")), HardwareMapping.Clean(Str(b, "Product")), HardwareMapping.Clean(Str(b, "SerialNumber")));
            });

            var bios = Section("BIOS", () =>
            {
                var b = Query(session, op, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
                return b is null ? null : new BiosInventory(HardwareMapping.Clean(Str(b, "Manufacturer")), HardwareMapping.Clean(Str(b, "SMBIOSBIOSVersion")), Date(b, "ReleaseDate"));
            });

            if (cpu is null && memory is null && board is null && bios is null && computer is null && caption is null && errors.Count > 0)
                throw new InvalidOperationException("No inventory section could be read: " + errors[0]);
            return new HardwareInventoryDto(errors.Count == 0 ? "Complete" : "Partial", DateTime.UtcNow, false, false, errors, osInfo, cpu, memory, board, bios, computer);
        }, ct);

    private static int? Int(CimInstance? i, string name) => Num(i, name) is { } v ? (int)v : null;

    private static long? Long(CimInstance? i, string name)
    {
        var v = i?.CimInstanceProperties[name]?.Value;
        return v is null ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime? Date(CimInstance? i, string name) =>
        i?.CimInstanceProperties[name]?.Value switch
        {
            DateTime d when d.Year > 1601 => d.ToUniversalTime(),
            string s => HardwareMapping.ParseCimDateTime(s),   // a CIM datetime delivered as text
            _ => null,
        };

    private static int? RegDword(CimSession session, string path, string value) =>
        RegCall(session, "GetDWORDValue", path, value)?.OutParameters["uValue"]?.Value is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
}
