# 07 — Memory Diagnostics

`MemoryDiagnosticsService` exposes `GC.GetGCMemoryInfo` (heap, fragmentation, load, committed) plus gen collection counts.

Admin `/diagnostics` + `DiagnosticsService` gRPC; SignalR `diagnostics` tick from `LiveTelemetryFeeder`.
