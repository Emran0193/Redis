# 08 — Persistence Audit

## AOF (`AofWriter`, `AofReplayEngine`, `DispatchingAofCommandReplayer`)

| Concern | Status | Evidence |
|---------|--------|----------|
| fsync policies | Implemented | `AofFlushPolicy` always / everysec / no |
| Always = ACK after fsync | Yes | Append waits on TCS completed in `WriteBatch` with `Flush(flushToDisk: true)` |
| Buffered channel | Yes | Bounded `Channel`, capacity `AofWriteQueueCapacity` (65536), `FullMode.Wait` |
| Atomic rewrite replace | Yes | Temp `.rewrite` then `File.Replace` / Move under `_streamLock` |
| Partial last record | Tolerated | Replay fail-closed except truncated final record (documented + code) |
| Relative TTL rewrite | Yes | `AofCommandRewriter` EXPIRE/SET EX → absolute |
| Replay via real handlers | Yes | `DispatchingAofCommandReplayer` — avoids divergent replayer bugs |
| Full-file load | **Fail** | `AofReplayEngine` buffers entire AOF |
| Rewrite exclusive | **Fail** | `RunExclusiveAsync` during encode |
| BGREWRITEAOF | Present | `BgRewriteAofCommandHandler` + `TryScheduleBackgroundRewrite` |
| CRC on AOF | **No** | Integrity is RESP structure only |

## Snapshot

| Concern | Status | Evidence |
|---------|--------|----------|
| Temp + rename | Yes | `snapshot.novdb.tmp` → move after rotate |
| CRC32 trailer | Yes | `Crc32` over version+flags+payload |
| Generations / CRC fallback | Yes | `SnapshotGenerationCount`, reader walks gens |
| Compression | Optional GZip | Doubles buffers when on |
| COW / non-blocking | **No** | Exclusive gate for full serialize |
| Versioning | Format v1 | `SnapshotFormat.CurrentVersion` |
| Consistency | Point-in-time under gate | Correct but stalls |

## Recovery policy

`DatabaseRecoveryService`: if AOF enabled and file non-empty → **AOF only** (snapshot ignored). Documented in `docs/Persistence.md`. Not Redis RDB+AOF hybrid. Ambiguous dual state is a warning, not a hard fail.

## Durability score: **6 / 10**

**Why not higher:** exclusive rewrite/snapshot stalls; full AOF in RAM; no AOF checksums; default `everysec` loses ≤1s; Always mode holds client + can hold `_dbGate` during EXEC.  
**Why not lower:** real fsync policies, atomic replace, CRC snapshots with generational fallback, handler-based replay, truncated-tail tolerance, BGREWRITEAOF.

## Crash scenarios

| Scenario | Expected | Covered by tests? |
|----------|----------|-------------------|
| Kill after Always ACK | Durable | Partial (recovery tests, not kill -9 harness) |
| Kill mid-append | Truncated tail OK | Logic yes; injection no |
| Kill mid-rewrite | Temp file leftover | Need verify startup ignores `.rewrite` |
| Corrupt middle AOF | Fail closed | `Restart_WithCorruptAof_FailsClosed` |
| Corrupt current snapshot | Fallback gen | `Snapshot_CorruptCurrent_FallsBackToPreviousGeneration` |
