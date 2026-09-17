# 05 — Memory Audit

## Summary

No `ArrayPool` / `MemoryPool` usage anywhere under `src/`. Large payloads and full-file buffers go straight to the LOH. Several structures duplicate UTF-8 as `string` + `byte[]`.

---

## Exact allocation sources

| Source | File / member | Problem | Replacement |
|--------|---------------|---------|-------------|
| Bulk payload copy | `RespParser.TryParseBulk` → `new byte[length]` | Unbounded; LOH if B≥85KB | Cap; rent from `ArrayPool`; or sequence slice until command completes |
| Array pre-alloc | `TryParseArray` → `new RespValue[count]` | OOM before parse | Cap count; grow list |
| Line decode | `Encoding.UTF8.GetString(lenLine)` | Per integer/header | `Utf8Parser.TryParse` on span |
| `RespValue.BulkString(string)` | `GetBytes` | Extra copy | Prefer byte APIs |
| Set members | `SetValue` `HashSet<string>` + `GetBytes` on read | Double representation | Byte-key set |
| Hash fields | `HashValue` string keys | String table growth | Byte keys |
| Snapshot body | `SnapshotWriter` `MemoryStream` → `ToArray()` (+ gzip buffer) | 2× dataset in RAM during snapshot | Stream CRC via incremental hasher to file |
| AOF replay | `AofReplayEngine.ReadLogAsync` full `CopyToAsync`/`ToArray` | AOF size ≈ heap | Streaming `PipeReader` over `FileStream` |
| AOF serialize | `RespWriter.Serialize` → `ToArray()` | Per mutation | Write to channel of `ReadOnlyMemory` rented buffers |
| SCAN/KEYS | `KeyspaceCommands` lists + `ToArray` | O(N) temporary | Cursor iterator |
| Pub/Sub message | `PublishAsync` clones subscriber snapshot | Per publish | Reuse / immutable snapshot |
| Session maps | `CommandProcessor._sessions` ConcurrentDictionary | Leak if disconnect cleanup fails | Already `OnDisconnectedAsync` — verify all paths |
| Auth failures | `AuthRateLimiter._failures` | Cleared on `Forget` — OK per connection; IP map would need TTL eviction | Bounded cache with expire |
| Metrics | OTel instruments | Acceptable | — |
| TLS cert | `TlsCertificateProvider` cached | OK under lock | — |

---

## LOH risk

Any bulk ≥ ~85,000 bytes lands on LOH (`RespParser`). Combined with missing max size, an attacker forces LOH fragmentation and GC pauses without completing a valid command.

Snapshot + gzip path: uncompressed body array + compressed array simultaneously (`SnapshotWriter.WriteSnapshotContentAsync`).

---

## Pool misuse

**None found** — pools are simply unused. Introducing pools without clear ownership (e.g. returning buffers still referenced by `RespValue`) would create use-after-return bugs; any pooling must redefine `RespValue` lifetime.

---

## Leak / dispose issues

| Issue | Evidence | Severity |
|-------|----------|----------|
| Pub/sub overflow sets `ShouldClose` but connection may sit in `ReadAsync` | `ConnectionPubSubSubscriber` + `ClientConnection` loop only checks `ShouldClose` after a command | P1 |
| `ClientConnection._writeLock` disposed in `DisposeAsync` | Correct if all writers await lock first | P3 |
| Timing wheel + KeyExpired events | Ensure unsubscribe on engine dispose (engine is singleton for process life) | P3 |
| Duplicate `ClientSession` vs `ClientSessionState` | Two session models (`Core.Sessions.ClientSession` vs `Networking.ClientSessionState`) — sync via processor; drift risk | P2 |

---

## Estimated memory model (architecture inference, not measured)

Per key roughly: `RedisKey` bytes + `DatabaseEntry` + value object + shard dictionary entry + optional generation entry.  
Sets add per-member string objects (high overhead vs Redis SDS).  
No jemalloc-style allocator; relies on .NET GC — fragmentation under LOH churn will dominate before algorithmic overhead for large values.
