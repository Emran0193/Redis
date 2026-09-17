# 06 — Concurrency Review

## Synchronization primitives in use

| Primitive | Locations |
|-----------|-----------|
| `SemaphoreSlim(1,1)` | `MemoryStorageEngine._dbGate`, `ClientConnection._writeLock` |
| `lock (shard.Sync)` | `MemoryStorageEngine` shard ops |
| `lock (_streamLock)` | `AofWriter` |
| `lock (_wheelLock)` | `TimingWheelExpirationScheduler` |
| `lock (_gate)` | `TlsCertificateProvider` |
| `Interlocked` | AOF rewrite flag, connection dispose, memory counters |
| `ConcurrentDictionary` | sessions, auth failures, pub/sub, connection tasks, subscriber cache |
| `Channel` (bounded) | AOF records, pub/sub outbox |
| `AsyncLocal<int>` | `_dbGateDepth` re-entrancy for EXEC |

---

## Issues

### P0 — Global gate + sync Wait (throughput + thread-pool starvation)

**Where:** `MemoryStorageEngine.EnterDb` / `ExitDb`.

**Wrong:** `_dbGate.Wait()` blocks a thread-pool thread for the entire op, including while waiting behind snapshot/EXEC.

**Repro:** 200 concurrent `GET`s while `SnapshotWriter` holds `RunExclusiveAsync` → all GETs block; thread pool may inject threads; latency spikes globally.

**Also:** Nested re-entry via `AsyncLocal` works for EXEC calling Mutate, but any code that hops threads without flowing `AsyncLocal` would deadlock (await without sync context is OK for AsyncLocal — it flows by default).

### P0 — Snapshot/AOF rewrite hold exclusive for I/O duration

**Where:** `SnapshotWriter.WriteEntriesAsync`, `AofWriter.RewriteFromStorageCoreAsync`.

**Repro:** Trigger snapshot on 1M keys; concurrent clients observe command latency ≈ snapshot duration.

### P0 — Pipeline pause deadlock (see perf)

Incomplete large RESP + 64KB pause: connection stuck; not a classic lock deadlock but a concurrency/protocol stall.

### P1 — EXEC holds exclusive across AOF durability waits

**Where:** `TransactionManager.ExecAsync` → `RunExclusiveAsync` → handlers → `CommandDispatcher` mutation sink → `AofWriter` Always-mode TCS wait.

**Repro:** `MULTI` + many `SET`s + `EXEC` with `AofFlushPolicy=always` → gate held for all fsyncs; other clients blocked.

### P1 — Auth rate limit race / reconnect

**Where:** `AuthRateLimiter` ConcurrentDictionary by connectionId; `Forget` on disconnect.

**Repro:** Parallel AUTH on same connection — generally OK (AddOrUpdate). Cross-connection brute force: new TCP each try resets state.

### P1 — Pub/Sub overflow disconnect race

**Where:** `ConnectionPubSubSubscriber.TryWrite` fails → `ShouldClose=true`; reader may be blocked in `ReadAsync` with no pending command.

**Repro:** Slow subscriber + fast `PUBLISH` → overflow; TCP stays open until idle timeout; hub may still list subscriber until disconnect cleanup.

### P2 — Accept loop vs reject

Awaiting `RejectConnectionAsync` serializes accepts under load of refused clients.

### P2 — Expiration during recovery

`TimingWheelExpirationScheduler` starts with Storage DI; recovery later. Wheel can fire expirations while AOF replay inserts keys.

**Repro:** Restart with AOF containing `SET`+`PEXPIREAT` near-now; wheel may delete during replay ordering surprises (depends on replay timestamps).

### P2 — AOF rewrite Interlocked vs waiting RewriteFromStorageAsync

`TryScheduleBackgroundRewrite` CAS; `RewriteFromStorageAsync` spins `Task.Delay(25)` waiting for flag — OK but busy-waits under contention.

### ABA

Delete+recreate WATCH: mitigated by `BumpGenerationLocked` / generation table (covered by unit test). Remaining ABA-like risk: watch key stringification collisions for binary keys (`TransactionManager.WatchAsync`).

### False sharing / priority inversion

Not evidenced in code review (no deliberate padding). Priority inversion: low-priority snapshot holds gate needed by latency-sensitive GETs — **classic inversion via shared lock**.

---

## What is done well (evidence)

- Shard lock never held while taking another shard lock (remarks + structure in `MemoryStorageEngine`).
- AOF bounded channel with Wait backpressure (avoids unbounded RAM) — trades latency for safety.
- Pub/sub TryWrite non-blocking (avoids publish blocking on slow consumer).
- Connection task tracking + drain on shutdown (`TcpServerHostedService.StopAsync`).
