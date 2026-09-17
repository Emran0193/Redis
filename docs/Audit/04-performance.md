# 04 — Performance Audit

Hot paths only. Complexity is asymptotic in keys (N), subscribers (S), payload bytes (B), shards (K).

---

## Hot path map

| Path | Entry | Complexity today | Target |
|------|-------|------------------|--------|
| RESP parse | `RespParser.TryParse` | O(B) + alloc O(B) | O(B) pooled |
| TCP receive | `SocketPipeline.PumpStreamToPipeAsync` | O(B) | OK |
| Command dispatch | `CommandDispatcher.DispatchAsync` | O(1) dict | OK |
| GET/SET | `MemoryStorageEngine.GetAsync`/`Mutate` | O(1) shard + **global gate Wait** | O(1) shard only |
| Serialize reply | `RespWriter.Write` / `Serialize` | O(B); `Serialize` → `ToArray()` | span write |
| AOF append | `AofWriter.AppendAsync` | O(B) + optional fsync wait | OK for Always |
| AOF rewrite | `RewriteFromStorageCoreAsync` | O(N) exclusive | O(N) unlocked |
| Snapshot | `SnapshotWriter.WriteEntriesAsync` | O(N) exclusive + full buffer | stream |
| Pub/Sub | `PubSubHub.PublishAsync` | O(S) + Task.WhenAll | O(S) |
| SCAN/KEYS | `KeyspaceCommands` | **O(N) per call** | O(batch) |

---

## Findings

### P0 — Unbounded RESP allocation
- **File:** `Protocol/RespParser.cs` `TryParseBulk` (`new byte[length]`), `TryParseArray` (`new RespValue[count]` before reading).
- **Allocations:** One LOH-candidate array per large bulk; adversarial `*2147483647` / `$1<<30`.
- **Fix:** Cap lengths; reject + disconnect before allocate.

### P0 — Pipe pause vs incomplete large message
- **File:** `Networking/SocketPipeline.cs` `pauseWriterThreshold: 65_536`; `ClientConnection.RunAsync` `AdvanceTo(buffer.Start, buffer.End)` when incomplete.
- **Effect:** Input pump blocks; connection cannot receive remainder → livelock until idle timeout.
- **Fix:** `pauseWriterThreshold >= MaxBulkBytes + overhead`, or reject oversize from length header alone.

### P0 — Global DB gate on every op
- **File:** `Storage/MemoryStorageEngine.cs` `EnterDb` → `_dbGate.Wait()` (sync).
- **Effect:** All GET/SET serialize; thread-pool threads block; sharding (`ShardCount=16`) does not buy parallel command throughput.
- **Complexity under contention:** effectively **O(queue depth)** wait, not O(1).

### P0 — Snapshot / AOF rewrite exclusive serialize
- **Files:** `Snapshot/SnapshotWriter.cs` (`RunExclusiveAsync` around scan+`WriteEntry`); `Aof/AofWriter.cs` `RewriteFromStorageCoreAsync`.
- **Effect:** Multi-second global stalls on large N; client timeouts.

### P1 — Per-command UTF-8 string churn
- **Files:** `RespValue.AsUtf8String`, every handler reading keys/fields; `SetValue.Add` `GetString`/`GetBytes`.
- **LINQ hot spots:** `HGETALL` `OrderBy().ToArray()`, `SMEMBERS`/`ZRANGE` `Select().ToArray()`, `COMMAND` `OrderBy`.
- **Fix:** Keep `ReadOnlySpan<byte>` / `RedisKey` through handlers; remove OrderBy unless Redis requires sorted HGETALL (Redis does **not** guarantee hash field order — sorting is pure cost).

### P1 — `RespWriter.Serialize` → `WrittenSpan.ToArray()`
- Used by AOF path; allocates full command buffer per append.
- Prefer writing directly into `PipeWriter` / rented `ArrayPool<byte>` (none used anywhere in repo).

### P1 — SCAN is O(N) every call
- **File:** `KeyspaceCommands.ScanCommandHandler` — materializes all keys into list, then slices by cursor.
- Not Redis SCAN complexity; destroys large-keyspace ops/sec.

### P2 — Accept reject awaits SendAsync
- **File:** `TcpServerHostedService.RejectConnectionAsync` awaited on accept loop.
- Slow rejected client delays AcceptAsync.

### P2 — Pub/Sub `Task.WhenAll` per publish
- **File:** `PubSubHub.PublishAsync` — fan-out allocates task list per message.
- Prefer sync TryWrite loop without WhenAll when outbox is non-blocking.

### P2 — Health check touches ScanAsync
- **File:** `NovaDbHealthCheck` — unnecessary work on every probe under `_dbGate`.

---

## Allocation inventory (hot path)

| Source | Kind | Avoidable? |
|--------|------|------------|
| `new byte[length]` bulk | heap / LOH | Cap + pool |
| `Encoding.UTF8.GetString` lines | string | Parse int from span |
| `RespValue` graph per command | objects | Arena / struct replies for simple types |
| `ToArray` on KEYS/SCAN/HGETALL | arrays | Stream / reuse buffer |
| AOF `Serialize` ToArray | array | Direct write |
| Snapshot `bodyStream.ToArray()` | large array | Stream to file |
| AOF replay full file ToArray | large array | Stream parse |
| No `ArrayPool` usage | — | Introduce |

---

## Recommendations (order)

1. Max payload + pipe threshold alignment.
2. Remove `_dbGate` from read path; async wait if exclusive needed.
3. Snapshot/rewrite without exclusive serialize.
4. Binary-safe collections (also perf: drop UTF-8 roundtrips).
5. Real SCAN cursor; kill HGETALL OrderBy.
6. ArrayPool for RESP write + AOF.
