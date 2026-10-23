using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Nexora.Application.Services;

namespace Nexora.Infrastructure.Wmi;

public sealed partial class WmiEndpointCollector
{
    public Task<LiveSampleDto> CollectPerformanceAsync(string host, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var session = Open(host);
        using var op = new CimOperationOptions { Timeout = TimeSpan.FromSeconds(2) };
        var cpu = ReadCpu(session, op);
        ct.ThrowIfCancellationRequested();
        var os = Query(session, op, "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
        var totalKb = Num(os, "TotalVisibleMemorySize");
        var freeKb = Num(os, "FreePhysicalMemory");
        long? total = totalKb is > 0 ? (long)(totalKb.Value * 1024) : null;
        long? free = freeKb is >= 0 && totalKb is > 0 && freeKb <= totalKb ? (long)(freeKb.Value * 1024) : null;
        var used = total.HasValue && free.HasValue ? total - free : null;
        double? percent = total is > 0 && used.HasValue ? Math.Round(100.0 * used.Value / total.Value, 1) : null;
        ct.ThrowIfCancellationRequested();
        return new LiveSampleDto(cpu, total, used, free, percent, DateTime.UtcNow);
    }, ct);

    private static double? ReadCpu(CimSession session, CimOperationOptions op)
    {
        var utility = SafeQuery(session, Ns, "SELECT PercentProcessorUtility FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'", op).FirstOrDefault();
        var cpu = CpuLoad.Resolve(Num(utility, "PercentProcessorUtility"), null, []);
        if (cpu is null)
        {
            var total = SafeQuery(session, Ns, "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'", op).FirstOrDefault();
            cpu = CpuLoad.Resolve(null, Num(total, "PercentProcessorTime"), []);
        }
        if (cpu is null)
        {
            var sockets = Query(session, op, "SELECT LoadPercentage, NumberOfLogicalProcessors FROM Win32_Processor");
            cpu = CpuLoad.Resolve(null, null, sockets.Select(p => (Num(p, "LoadPercentage"), Num(p, "NumberOfLogicalProcessors"))));
        }
        return cpu;
    }
}
