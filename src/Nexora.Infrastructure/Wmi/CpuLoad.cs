namespace Nexora.Infrastructure.Wmi;

/// <summary>Total CPU utility follows Windows' frequency-aware utilization; processor-time and weighted socket load are fallbacks.</summary>
public static class CpuLoad
{
    public static double? Resolve(double? utility, double? processorTime, IEnumerable<(double? Load, double? LogicalProcessors)> sockets)
    {
        if (utility is { } u && double.IsFinite(u) && u is >= 0 and <= 1_000) return Math.Round(Math.Min(u, 100), 1);
        if (processorTime is >= 0 and <= 100 and var time && double.IsFinite(time)) return Math.Round(time, 1);
        var valid = sockets.Where(s => s.Load is >= 0 and <= 100 && double.IsFinite(s.Load.Value)).ToArray();
        if (valid.Length == 0) return null;
        static double Weight(double? count) => count is > 0 && double.IsFinite(count.Value) ? count.Value : 1;
        return Math.Round(valid.Sum(s => s.Load!.Value * Weight(s.LogicalProcessors)) / valid.Sum(s => Weight(s.LogicalProcessors)), 1);
    }
}
