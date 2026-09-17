using System.Runtime;
using NovaDB.Contracts.Admin;

namespace NovaDB.Server.Diagnostics;

/// <summary>
/// Captures GC / LOH / fragmentation diagnostics for the Admin Diagnostics page.
/// </summary>
public sealed class MemoryDiagnosticsService
{
    /// <summary>
    /// Captures a point-in-time memory diagnostics snapshot.
    /// </summary>
    public MemoryDiagnostics Capture()
    {
        var info = GC.GetGCMemoryInfo();
        return new MemoryDiagnostics
        {
            TotalAvailableBytes = info.TotalAvailableMemoryBytes,
            HeapSizeBytes = info.HeapSizeBytes,
            FragmentedBytes = info.FragmentedBytes,
            MemoryLoadBytes = info.MemoryLoadBytes,
            HighMemoryLoadThresholdBytes = info.HighMemoryLoadThresholdBytes,
            TotalCommittedBytes = info.TotalCommittedBytes,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            CompactionModeLoh = GCSettings.LargeObjectHeapCompactionMode
                == GCLargeObjectHeapCompactionMode.CompactOnce,
            Note = "ArrayPool shared stats are not publicly exposed; GCMemoryInfo drives this view."
        };
    }
}
