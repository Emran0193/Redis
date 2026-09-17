# Benchmarks

BenchmarkDotNet project: `benchmarks/NovaDB.Benchmarks`.

## How to run

```bash
dotnet run -c Release --project benchmarks/NovaDB.Benchmarks
```

## Targets (modern hardware goals)

| Metric | Target |
| --- | --- |
| GET P50 | &lt; 100 µs |
| SET P50 | &lt; 120 µs |
| Throughput | 500k+ ops/sec |
| Allocations per GET | 0 B (hot path goal) |
| Concurrent clients | 10,000+ |

## Suites

- `SetGetThroughputBenchmarks` — in-process storage SET/GET
- `RespParserBenchmarks` — parse allocations / throughput
- `PersistenceBenchmarks` — snapshot write speed (when enabled)

Reports are written under `BenchmarkDotNet.Artifacts/` and summarized here after local runs.
