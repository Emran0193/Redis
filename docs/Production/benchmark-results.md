# Benchmark results

Placeholder for BenchmarkDotNet artifacts.

## How to generate

```bash
dotnet run -c Release --project benchmarks/NovaDB.Benchmarks --filter *
```

Copy the console summary or `BenchmarkDotNet.Artifacts/results/*.md` here after a Release run on the target SKU.

## Suites

| Suite | Focus |
| --- | --- |
| SetGetThroughputBenchmarks | Storage SET/GET |
| MgetThroughputBenchmarks | Multi-key GET |
| RespParserBenchmarks | Single-frame parse |
| PipelineParseBenchmarks | Pipelined parse |
| PubSubBenchmarks | Publish fan-out |
| SnapshotScanBenchmarks | Full key scan cost |
