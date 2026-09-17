# 12 — Transactions Audit

**Manager:** `TransactionManager`  
**Handlers:** `TransactionCommands.cs`  
**Session:** `ClientSession` (`InMulti`, `QueuedCommands`, `WatchedKeyVersions`)

---

| Feature | Status | Notes |
|---------|--------|-------|
| MULTI | Yes | Nested MULTI rejected |
| EXEC | Yes | Under `RunExclusiveAsync` |
| DISCARD | Yes | Clears queue + watches via `ClearTransaction` |
| WATCH | Yes | Versions from `GetKeyVersionAsync` |
| UNWATCH | **Missing** | No handler; only DISCARD/ClearTransaction |
| Optimistic concurrency | Generations | Delete/recreate ABA fixed in storage |
| Rollback | N/A | Redis EXEC doesn't rollback prior effects; NovaDB same — queue not applied if watch fails (null bulk) |

## Stale watches

- Generations survive delete — good.
- Watch keys stored as `keys[i].ToString()` → binary/UTF-8 collision risk (`TransactionManager.WatchAsync`).
- Watches not cleared on successful EXEC? Cleared explicitly before applying queue (`WatchedKeyVersions.Clear` in `ExecAsync`) — OK.
- Mid-EXEC handler failure: prior subcommands in the loop may already have mutated storage (Redis also returns per-command errors in the result array without rollback) — acceptable Redis-like behavior; ensure exceptions map to error RespValues rather than aborting half-applied without reply (dispatcher catches `NovaDbException`).

## EXEC + AOF

Exclusive section includes per-command mutation sink; with `always` fsync, EXEC latency and global stall scale with queue length × disk.

## Verdict

Core MULTI/EXEC/WATCH semantics are intentionally Redis-like and partially tested (`TransactionTests`). Gaps: UNWATCH, binary watch keys, exclusive+fsync coupling.
