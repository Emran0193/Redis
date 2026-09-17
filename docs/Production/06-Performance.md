# 06 — Performance

## Hot-path posture

- RESP parse uses `System.IO.Pipelines`
- Command handlers use `ValueTask` end-to-end
- Mutation fan-out is async; AOF uses a bounded channel
- HINCRBY uses `Utf8Parser` where applicable

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/NovaDB.Benchmarks
```

Covered scenarios: **SET**, **GET**, **MGET**, pipeline-style batches, PubSub publish path, snapshot scan/serialize helpers.

Capture results into `docs/Production/benchmark-results.md`.

## Guidance

Prefer correctness over micro-optimizations. Profile with a release build under realistic key sizes before changing parsers.
