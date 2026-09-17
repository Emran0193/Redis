# 16 — Scalability

Inferences from architecture only — **no invented benchmark numbers**.

## Target scenarios

| Target | Feasible with current design? | Bottleneck |
|--------|-------------------------------|------------|
| 100K concurrent connections | Unlikely without major work | Per-conn pipes/tasks/sessions; default max 10k; GC; thread pool; no IOCP tuning evidenced |
| 1M keys | Plausible for memory if values small | Snapshot/SCAN/FLUSH O(N); exclusive snapshot duration |
| 10M keys | Risky | Snapshot/rewrite exclusive time; SCAN materialization; heap size; AOF rewrite encode |
| 500K ops/sec | Unproven at TCP edge; in-process path shows parallel speedup post-gate removal | Per-conn pipes/tasks; AOF Always; EXEC exclusive; accept loop single-threaded |

## Likely throughput shape

Architecture **previously** implied single-mutex command rate via `MemoryStorageEngine._dbGate`.
Sprint 2 removed that gate from ordinary ops (exclusive flag remains for EXEC / snapshot / AOF rewrite only).

**Measured (in-process, 2026-09-16, `GateFreeThroughputLoadTests`):** parallel distinct-key SETs across 8 clients beat a sequential loop of the same op count (asserts `parallelElapsed < sequentialElapsed` and `>1000` parallel ops/s). Exact ops/s varies by machine; the regression guard is the speedup assertion in CI.

Sharding now multiplies ordinary write parallelism for disjoint keys. Pub/sub and Always-AOF fsync remain separate limiters.

## Multi-acceptor decision

**Deferred.** Command-path serialization is no longer the primary evidence gap. Invest in SO_REUSEPORT / multi-acceptor only after a TCP accept-rate soak shows the listener as the bottleneck — not before.

## Horizontal scale

No cluster, no replication implementation (`Core/Replication` stubs). Single-node only. `docs/Replication.md` is aspirational.

## Vertical scale levers (if fixed)

1. ~~Remove global gate from reads / most writes.~~ **Done (Sprint 2).**
2. ~~Non-blocking snapshot.~~ **Done (Sprint 3 short freeze).**
3. ~~Cap and stream RESP.~~ **Done (Sprint 1).**
4. ~~True SCAN.~~ **Done (Sprint 2 shard cursor).**
5. Connection pooling / SO_REUSEPORT multi-acceptor — **deferred** pending TCP accept-rate evidence (see above).
